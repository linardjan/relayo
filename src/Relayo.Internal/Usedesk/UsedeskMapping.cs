using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relayo.Core.Channels;
using Relayo.Core.Sites;
using Relayo.Core.Users;

namespace Relayo.Internal.Usedesk;

/// <summary>Виджет Usedesk в виде настроек Relayo и что из него не переносится.</summary>
public sealed record WidgetMapping(WidgetInput Input, string? TelegramUsername, string? WhatsAppPhone, IReadOnlyList<string> Notes);

/// <summary>
/// Сопоставление настроек Usedesk с Relayo (docs/usedesk-migration.md, разделы 2.1, 2.7, 2.8). Без обращений к базам:
/// всё, что здесь решается, проверяют модульные тесты.
/// </summary>
public static partial class UsedeskMapping
{
    /// <summary>admin → Admin, employee → Operator; support (учётки Usedesk) и bot не переносятся.</summary>
    public static UserRole? Role(string role) => role.Trim().ToLowerInvariant() switch
    {
        "admin" => UserRole.Admin,
        "employee" => UserRole.Operator,
        _ => null,
    };

    /// <summary>Группа сотрудников: имя в Relayo (до 100 символов) или почему не переносится (docs/usedesk-migration.md, 2.11).</summary>
    public static (string? Name, string? Problem) Group(UsedeskGroup g)
    {
        if (g.Deleted)
            return (null, "Deleted in Usedesk: not imported");
        if (g.Demo)
            return (null, "Demo group: not imported");
        var name = Clip(g.Name, AgentGroup.NameMaxLength);
        return name.Length == 0 ? (null, "The group has no name") : (name, null);
    }

    /// <summary>
    /// Группы сотрудника в Relayo по его группам в Usedesk: к тем, что у него уже есть, добавляются перенесённые
    /// (user_group_id и default_group); группа по умолчанию — прежняя, иначе default_group, иначе user_group_id.
    /// Не перенесённые ещё группы пропускаются: их допишет импорт групп. null — менять нечего.
    /// </summary>
    public static (List<Guid> GroupIds, Guid? DefaultGroupId)? UserGroups(UsedeskUser u, IReadOnlyDictionary<int, Guid> groups,
        IReadOnlyList<Guid> current, Guid? currentDefault)
    {
        Guid? Find(int? id) => id is { } key && groups.TryGetValue(key, out var g) ? g : null;
        var main = Find(u.GroupId);
        var preferred = Find(u.DefaultGroupId);
        var added = new[] { main, preferred }.OfType<Guid>().Where(g => !current.Contains(g)).Distinct().ToList();
        var defaultGroup = currentDefault ?? preferred ?? main;
        if (added.Count == 0 && defaultGroup == currentDefault)
            return null;
        return ([.. current, .. added], defaultGroup);
    }

    /// <summary>
    /// objectGUID из users_guid_migration: строкой (3f2504e0-…) или base64 байтов, как их отдаёт LDAP.
    /// Байты objectGUID в AD — в том же порядке, что у .NET <see cref="Guid(byte[])"/>.
    /// </summary>
    public static Guid? ParseGuid(string? value)
    {
        value = value?.Trim().Trim('{', '}');
        if (string.IsNullOrEmpty(value))
            return null;
        if (Guid.TryParse(value, out var guid))
            return guid == Guid.Empty ? null : guid;
        var bytes = new byte[16];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == 16 ? new Guid(bytes) : null;
    }

    /// <summary>Пароль, зашифрованный Laravel (APP_KEY Usedesk), а не открытым текстом: base64 от {"iv":…}.</summary>
    public static bool IsLaravelEncrypted(string? value) =>
        value is not null && value.StartsWith("eyJpdiI6", StringComparison.Ordinal);

    /// <summary>imap_encrypted / smtp_encrypted Usedesk: ssl, tls / starttls или пусто (без шифрования).</summary>
    public static MailSecurity Security(string? encrypted, bool unencrypted)
    {
        var value = encrypted?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Contains("ssl"))
            return MailSecurity.Ssl;
        // В Laravel (SwiftMailer) «tls» значит STARTTLS.
        if (value.Contains("tls"))
            return MailSecurity.StartTls;
        return unencrypted || value is "" or "none" or "no" ? MailSecurity.None : MailSecurity.StartTls;
    }

    /// <summary>Адрес ящика: имя пользователя IMAP, если это адрес, иначе основной или адрес отправителя.</summary>
    public static string? MailboxAddress(UsedeskChannel c) =>
        new[] { c.ImapUsername, c.MainEmail, c.FromEmail }.FirstOrDefault(v => v is not null && v.Contains('@'))?.Trim();

    /// <summary>Почтовый канал Usedesk в виде настроек Relayo или причина, по которой его нельзя перенести.</summary>
    public static (EmailChannelInput? Input, string? Problem) EmailChannel(UsedeskChannel c, Guid siteId)
    {
        if (!string.Equals(c.IncomingConnection, "external", StringComparison.OrdinalIgnoreCase))
            return (null, $"The {c.IncomingConnection} mailbox of Usedesk is not supported: Relayo connects only by IMAP/SMTP");
        var address = MailboxAddress(c);
        if (string.IsNullOrWhiteSpace(c.ImapHost) || string.IsNullOrWhiteSpace(c.SmtpHost) || address is null)
            return (null, "IMAP or SMTP settings are incomplete");
        if (string.IsNullOrEmpty(c.ImapPassword) || string.IsNullOrEmpty(c.SmtpPassword))
            return (null, "No password in Usedesk");
        if (IsLaravelEncrypted(c.ImapPassword) || IsLaravelEncrypted(c.SmtpPassword))
            return (null, "Passwords are encrypted in Usedesk: enter them on the channel page");

        var imapSecurity = Security(c.ImapEncrypted, c.Unencrypted);
        var smtpSecurity = Security(c.SmtpEncrypted, c.Unencrypted);
        var smtpAddress = new[] { c.FromEmail, c.SmtpUsername, address }.First(v => v is not null && v.Contains('@'))!.Trim();
        return (new EmailChannelInput(
            Clip(c.Name, Core.Channels.EmailChannel.NameMaxLength) is { Length: > 0 } name ? name : address,
            siteId,
            IsEnabled: true,
            ForwardCreatesTicket: false,
            c.Quotes ? EmailQuoting.SendToClient : EmailQuoting.DoNotSend,
            new MailServerInput(address, c.ImapHost.Trim(), c.ImapPort ?? (imapSecurity == MailSecurity.Ssl ? 993 : 143), imapSecurity,
                c.ImapUsername, c.ImapPassword),
            new MailServerInput(smtpAddress, c.SmtpHost.Trim(),
                c.SmtpPort ?? (smtpSecurity == MailSecurity.Ssl ? 465 : smtpSecurity == MailSecurity.StartTls ? 587 : 25), smtpSecurity,
                c.SmtpUsername, c.SmtpPassword)), null);
    }

    /// <summary>
    /// Виджет Usedesk → настройки Relayo. Пункты — из messenger_settings того же канала: чат, Telegram (бот выбирается
    /// по имени из ссылки t.me), WhatsApp (номер), ссылки на соцсети. Чего у нас нет — в <see cref="WidgetMapping.Notes"/>.
    /// </summary>
    public static WidgetMapping Widget(UsedeskWidget w, UsedeskMessenger? m, IdentificationMode mode)
    {
        var notes = new List<string>();
        var items = new List<LauncherItem> { new(LauncherItemType.Chat, WidgetOptions.DefaultLabel(LauncherItemType.Chat)) };
        string? telegram = null;
        string? whatsApp = null;

        if (m is not null)
        {
            foreach (var (type, link) in ActiveItems(m.ActiveJson, notes))
            {
                switch (type)
                {
                    case "CHAT_CALLBACK_FORM" or "CHAT":
                        break;
                    case "TG" or "TELEGRAM":
                        telegram = TelegramUsername(link ?? m.TgUrl);
                        if (telegram is null)
                            notes.Add("Telegram item: no bot link");
                        break;
                    case "WHATSAPP" or "WA":
                        whatsApp = Digits(link);
                        break;
                    default:
                        var url = link ?? type switch
                        {
                            "VK" => m.VkUrl,
                            "FB" or "FACEBOOK" => m.FacebookUrl,
                            "VIBER" => m.ViberUrl,
                            "OK" => m.OkUrl,
                            "INSTA" or "INSTAGRAM" => m.InstaUrl,
                            _ => null,
                        };
                        if (HttpUrl(url) is { } http)
                            items.Add(new LauncherItem(LauncherItemType.Link, LinkLabel(type), http));
                        else
                            notes.Add($"{type} item is skipped: Relayo links open only http(s) addresses");
                        break;
                }
            }
        }
        if (items.Count > WidgetOptions.MaxItems - 2)
        {
            notes.Add($"Only the first {WidgetOptions.MaxItems - 2} links are kept");
            items = items.Take(WidgetOptions.MaxItems - 2).ToList();
        }

        if (w.TopicsRequired)
            notes.Add("Topics are not supported yet");
        if (w.CallbackEnabled)
            notes.Add("Callback form is not supported yet");
        if (w.AllowRating)
            notes.Add("Chat rating is not supported yet");
        if (w.CustomFields is { Length: > 2 })
            notes.Add("Pre-chat custom fields are not supported yet");
        notes.Add("Upload the button icon and set the greeting on the widget page");

        var tooltip = Clip(StripHtml(w.BadgeText), WidgetOptions.TooltipTextMaxLength);
        var options = new WidgetOptions
        {
            AllowAttachments = w.Attachment,
            ZIndex = w.ZIndex > 0 ? w.ZIndex : WidgetOptions.Default.ZIndex,
            HideOnOutsideClick = w.HideOnOutsideClick,
            ButtonColor = Color(w.ButtonColor),
            OperatorName = Clip(w.OperatorName, WidgetOptions.OperatorNameMaxLength) is { Length: > 0 } op ? op : null,
            TooltipMode = tooltip.Length == 0 ? WidgetTooltipMode.Never : w.ShowTooltipType == 2 ? WidgetTooltipMode.Hover : WidgetTooltipMode.Always,
            TooltipText = tooltip,
            TooltipBackground = Color(w.BadgeColor) ?? WidgetOptions.Default.TooltipBackground,
            TooltipColor = Color(w.BadgeTextColor) ?? WidgetOptions.Default.TooltipColor,
            TooltipTextSize = Math.Clamp(w.BadgeFontSize, 10, 32),
            Items = items,
        };

        var name = Clip(StripHtml(w.Name), 200) is { Length: > 0 } n ? n : $"Usedesk widget {w.Id}";
        var title = Clip(StripHtml(w.Title), 100) is { Length: > 0 } t ? t : name.Length <= 100 ? name : "Support";
        var locale = w.Language.Trim().ToLowerInvariant() is { Length: > 0 } l && LocalePattern().IsMatch(l) ? l : "ru";
        var input = new WidgetInput(name, mode, title, string.Empty, Color(w.Color) ?? "#1677ff",
            w.Alignment.Equals("left", StringComparison.OrdinalIgnoreCase) ? WidgetPosition.Left : WidgetPosition.Right,
            locale, Options: options);
        return new WidgetMapping(input, telegram, whatsApp, notes);
    }

    /// <summary>
    /// Пункты из active_json. Формат не задокументирован: принимаются массив объектов {"type", "externalLink"}
    /// и объект «тип → включён/ссылка».
    /// </summary>
    internal static IEnumerable<(string Type, string? Link)> ActiveItems(string? json, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [("CHAT", null)];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new List<(string, string?)>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        result.Add((item.GetString()!.ToUpperInvariant(), null));
                    else if (item.ValueKind == JsonValueKind.Object && Prop(item, "type") is { } type)
                        result.Add((type.ToUpperInvariant(), Prop(item, "externalLink") ?? Prop(item, "link") ?? Prop(item, "url")));
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind is JsonValueKind.False or JsonValueKind.Null || p.Value is { ValueKind: JsonValueKind.Number } n && n.GetDouble() == 0)
                        continue;
                    result.Add((p.Name.ToUpperInvariant(), p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null));
                }
            }
            return result;
        }
        catch (JsonException)
        {
            notes.Add("Could not read the widget items (active_json): only the chat item is imported");
            return [("CHAT", null)];
        }
    }

    private static string? Prop(JsonElement e, string name) =>
        e.EnumerateObject().FirstOrDefault(p => p.NameEquals(name) || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is
            { Value.ValueKind: JsonValueKind.String } p
            ? p.Value.GetString()
            : null;

    /// <summary>https://t.me/relayo_bot, t.me/relayo_bot?start=x, @relayo_bot → relayo_bot.</summary>
    public static string? TelegramUsername(string? link)
    {
        var m = TelegramPattern().Match(link?.Trim() ?? string.Empty);
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;
        var text = TagPattern().Replace(BreakPattern().Replace(html, " "), string.Empty);
        return WhitespacePattern().Replace(WebUtility.HtmlDecode(text), " ").Trim();
    }

    /// <summary>#abc / #aabbcc / rgb(...) → #aabbcc; иначе null.</summary>
    public static string? Color(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        if (HexPattern().IsMatch(value))
            return value.Length == 4 ? $"#{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}".ToLowerInvariant() : value.ToLowerInvariant();
        var rgb = RgbPattern().Match(value);
        return rgb.Success
            ? $"#{byte.Parse(rgb.Groups[1].Value):x2}{byte.Parse(rgb.Groups[2].Value):x2}{byte.Parse(rgb.Groups[3].Value):x2}"
            : null;
    }

    private static string? HttpUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && url!.Trim().Length <= WidgetOptions.UrlMaxLength
            ? url.Trim()
            : null;

    private static string LinkLabel(string type) => type switch
    {
        "VK" => "VK",
        "FB" or "FACEBOOK" => "Facebook",
        "OK" => "OK",
        "INSTA" or "INSTAGRAM" => "Instagram",
        "VIBER" => "Viber",
        _ => Clip(type, WidgetOptions.LabelMaxLength),
    };

    private static string? Digits(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 8 ? digits : null;
    }

    public static string Clip(string? value, int max)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= max ? value : value[..max].TrimEnd();
    }

    [GeneratedRegex(@"^(?:https?://)?(?:t\.me|telegram\.me)/([A-Za-z0-9_]{5,32})|^@([A-Za-z0-9_]{5,32})$")]
    private static partial Regex TelegramPattern();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})")]
    private static partial Regex RgbPattern();

    [GeneratedRegex("^[a-z]{2}(-[a-z]{2})?$")]
    private static partial Regex LocalePattern();
}
