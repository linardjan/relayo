using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Relayo.Core.Data;
using Relayo.Core.Security;

namespace Relayo.Core.Sites;

public sealed record SiteKeyView(Guid Id, string KeyId, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

public sealed record SiteView(
    Guid Id,
    string Code,
    string Name,
    IReadOnlyList<string> AllowedOrigins,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SiteKeyView> Keys,
    int WidgetCount);

/// <summary>Новый ключ: секрет показывается один раз, потом хранится только зашифрованным.</summary>
public sealed record CreatedSiteKey(SiteKeyView Key, string Secret);

public sealed record SiteInput(string Name, IReadOnlyList<string> AllowedOrigins);

public sealed record WidgetView(
    Guid Id,
    Guid SiteId,
    string SiteCode,
    string Name,
    IdentificationMode IdentificationMode,
    string Title,
    string Greeting,
    string AccentColor,
    WidgetPosition Position,
    string DefaultLocale,
    Guid? TelegramBotId,
    DateTimeOffset CreatedAt,
    string PublicKey,
    int? PublishedVersion,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt,
    bool HasUnpublishedChanges,
    WidgetOptions Options);

public sealed record WidgetInput(
    string Name,
    IdentificationMode IdentificationMode,
    string Title,
    string Greeting,
    string AccentColor,
    WidgetPosition Position,
    string DefaultLocale,
    Guid? TelegramBotId = null,
    WidgetOptions? Options = null);

/// <summary>Сколько открытых тикетов идёт через каждый канал: число на плитке канала.</summary>
public sealed record ChannelCounts(IReadOnlyDictionary<Guid, int> Widgets, IReadOnlyDictionary<Guid, int> TelegramBots);

/// <summary>Настройки сайтов, ключей и виджетов (раздел «Настройки», только администратор).</summary>
public sealed partial class SiteSettingsService(
    RelayoDbContext db,
    SecretProtector protector,
    IFileStorage storage,
    TimeProvider time)
{
    // ---- Сайты ----

    public async Task<IReadOnlyList<SiteView>> ListSitesAsync(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct);
        var keys = (await db.SiteKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct))
            .ToLookup(k => k.SiteId);
        var widgets = await db.Widgets.AsNoTracking().GroupBy(w => w.SiteId)
            .Select(g => new { SiteId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SiteId, g => g.Count, ct);

        return sites.Select(s => ToView(s, keys[s.Id], widgets.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SiteView> CreateSiteAsync(string code, SiteInput input, CancellationToken ct)
    {
        code = code.Trim().ToLowerInvariant();
        if (!SiteCodePattern().IsMatch(code))
            throw new DomainException(ErrorCodes.InvalidInput,
                "Site code must be 1-64 characters: lowercase letters, digits, '-' and '_'");
        if (await db.Sites.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(ErrorCodes.SiteCodeTaken, "A site with this code already exists");

        var site = new Site(Guid.CreateVersion7(), code, ValidName(input.Name), ValidOrigins(input.AllowedOrigins),
            time.GetUtcNow());
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        return ToView(site, [], 0);
    }

    public async Task<SiteView> UpdateSiteAsync(Guid siteId, SiteInput input, CancellationToken ct)
    {
        var site = await FindSiteAsync(siteId, ct);
        site.Update(ValidName(input.Name), ValidOrigins(input.AllowedOrigins));
        await db.SaveChangesAsync(ct);
        return (await ListSitesAsync(ct)).Single(s => s.Id == siteId);
    }

    public async Task<CreatedSiteKey> CreateKeyAsync(Guid siteId, CancellationToken ct)
    {
        var site = await FindSiteAsync(siteId, ct);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var keyId = $"{site.Code}-{time.GetUtcNow():yyyyMMdd}-{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(4))}";
        var key = new SiteKey(site.Id, keyId, protector.Protect(secret), time.GetUtcNow());
        db.SiteKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new CreatedSiteKey(ToView(key), secret);
    }

    public async Task<SiteKeyView> RevokeKeyAsync(Guid siteId, Guid keyId, CancellationToken ct)
    {
        var key = await db.SiteKeys.SingleOrDefaultAsync(k => k.Id == keyId && k.SiteId == siteId, ct)
                  ?? throw new DomainException(ErrorCodes.KeyNotFound, "Key not found");
        key.Revoke(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return ToView(key);
    }

    // ---- Каналы ----

    /// <summary>
    /// Открытые тикеты по каналам: тикет считается в канале, если в нём писали через этот виджет или бота.
    /// Начинаем с открытых тикетов — их немного, поэтому запрос не проходит по всей истории сообщений.
    /// </summary>
    public async Task<ChannelCounts> CountOpenTicketsAsync(CancellationToken ct)
    {
        var rows = await (
                from t in db.Tickets
                where t.Status != Conversations.TicketStatus.Closed
                join c in db.Chats on t.Id equals c.TicketId
                join m in db.Messages on c.Id equals m.ChatId
                join i in db.ClientIdentities on m.VisitorIdentityId equals i.Id
                select new { TicketId = t.Id, i.WidgetId, i.ChannelId, i.Type })
            .Distinct()
            .ToListAsync(ct);

        return new ChannelCounts(
            rows.Where(r => r.WidgetId != null).GroupBy(r => r.WidgetId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(r => r.TicketId).Distinct().Count()),
            rows.Where(r => r.Type == Clients.IdentityType.Telegram && r.ChannelId != null).GroupBy(r => r.ChannelId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(r => r.TicketId).Distinct().Count()));
    }

    // ---- Виджеты ----

    public async Task<IReadOnlyList<WidgetView>> ListWidgetsAsync(CancellationToken ct) =>
        await db.Widgets.AsNoTracking()
            .OrderBy(w => w.Site.Code).ThenBy(w => w.Name)
            .Select(w => new WidgetView(w.Id, w.SiteId, w.Site.Code, w.Name, w.IdentificationMode, w.Title, w.Greeting,
                w.AccentColor, w.Position, w.DefaultLocale, w.TelegramBotId, w.CreatedAt, w.PublicKey, w.PublishedVersion,
                w.PublishedAt, w.UpdatedAt, w.PublishedAt == null || w.UpdatedAt > w.PublishedAt, w.Options))
            .ToListAsync(ct);

    public async Task<WidgetView> CreateWidgetAsync(Guid siteId, WidgetInput input, CancellationToken ct)
    {
        var site = await FindSiteAsync(siteId, ct);
        var widget = new Widget(Guid.CreateVersion7(), site.Id, ValidName(input.Name), input.IdentificationMode,
            time.GetUtcNow());
        await ApplyAsync(widget, input, ct);
        db.Widgets.Add(widget);
        await db.SaveChangesAsync(ct);
        return (await ListWidgetsAsync(ct)).Single(w => w.Id == widget.Id);
    }

    public async Task<WidgetView> UpdateWidgetAsync(Guid widgetId, WidgetInput input, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(w => w.Id == widgetId, ct)
                     ?? throw new DomainException(ErrorCodes.WidgetNotFound, "Widget not found");
        await ApplyAsync(widget, input, ct);
        await db.SaveChangesAsync(ct);
        return (await ListWidgetsAsync(ct)).Single(w => w.Id == widget.Id);
    }

    private async Task ApplyAsync(Widget widget, WidgetInput input, CancellationToken ct)
    {
        var title = input.Title?.Trim() ?? string.Empty;
        var greeting = input.Greeting?.Trim() ?? string.Empty;
        var color = input.AccentColor?.Trim() ?? string.Empty;
        var locale = input.DefaultLocale?.Trim().ToLowerInvariant() ?? string.Empty;
        if (title.Length is 0 or > 100)
            throw new DomainException(ErrorCodes.InvalidInput, "Title must be 1-100 characters");
        if (greeting.Length > 1000)
            throw new DomainException(ErrorCodes.InvalidInput, "Greeting must be up to 1000 characters");
        if (!ColorPattern().IsMatch(color))
            throw new DomainException(ErrorCodes.InvalidInput, "Accent color must look like #1677ff");
        if (!LocalePattern().IsMatch(locale))
            throw new DomainException(ErrorCodes.InvalidInput, "Locale must look like en or en-US");
        if (!Enum.IsDefined(input.IdentificationMode) || !Enum.IsDefined(input.Position))
            throw new DomainException(ErrorCodes.InvalidInput, "Unknown identification mode or position");

        if (input.TelegramBotId is { } botId && !await db.TelegramBots.AnyAsync(b => b.Id == botId, ct))
            throw new DomainException(ErrorCodes.BotNotFound, "Telegram bot not found");

        // Без настроек (старые клиенты API) — прежние настройки виджета, иначе присланные.
        // Картинку кнопки меняет только загрузка: здесь её можно лишь убрать.
        var options = input.Options ?? widget.Options;
        if (options.ButtonIcon is not null && options.ButtonIcon != widget.Options.ButtonIcon)
            options = options with { ButtonIcon = widget.Options.ButtonIcon };
        options = options.Normalize(hasTelegramBot: input.TelegramBotId is not null);

        widget.Configure(ValidName(input.Name), input.IdentificationMode, title, greeting, color, input.Position, locale,
            input.TelegramBotId, options, time.GetUtcNow());
    }

    /// <summary>Максимальный размер картинки кнопки: 512×512 проверяет админка, здесь — объём файла.</summary>
    public const int ButtonIconMaxBytes = 512 * 1024;

    /// <summary>
    /// Своя картинка кнопки чата. Лежит в публичном бакете виджетов рядом со скриптом
    /// (widget_&lt;ключ&gt;_icon_&lt;случайное&gt;.&lt;расширение&gt;): новое имя у каждой загрузки, поэтому её можно кэшировать навсегда.
    /// </summary>
    public async Task<WidgetView> SetButtonIconAsync(Guid widgetId, Files.FileUpload upload, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(w => w.Id == widgetId, ct)
                     ?? throw new DomainException(ErrorCodes.WidgetNotFound, "Widget not found");
        if (!storage.IsConfigured)
            throw new DomainException(ErrorCodes.StorageNotConfigured, "File storage (S3) is not configured");
        if (upload.Content.Length > ButtonIconMaxBytes)
            throw new DomainException(ErrorCodes.AttachmentTooLarge, "The image must be up to 512 KB");
        var file = Files.AttachmentPolicy.Check([upload],
            new Files.AttachmentLimits(ButtonIconMaxBytes, ["image/png", "image/jpeg", "image/gif", "image/webp"])).Single();

        var extension = file.ContentType.Split('/')[1].Replace("jpeg", "jpg");
        var name = $"widget_{widget.PublicKey}_icon_{WidgetKeys.Create()}.{extension}";
        await storage.PutWidgetFileAsync(name, file.Content, "public, max-age=31536000, immutable", ct, file.ContentType);
        widget.SetButtonIcon(name, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return (await ListWidgetsAsync(ct)).Single(w => w.Id == widget.Id);
    }

    /// <summary>
    /// Удаляет виджет. Переписка остаётся: посетители и сообщения не удаляются. Опубликованный файл
    /// widget_&lt;ключ&gt;.js убирается из S3, чтобы сайты перестали показывать виджет.
    /// </summary>
    public async Task DeleteWidgetAsync(Guid widgetId, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(w => w.Id == widgetId, ct)
                     ?? throw new DomainException(ErrorCodes.WidgetNotFound, "Widget not found");
        if (widget.PublishedVersion is not null && storage.IsConfigured)
            await storage.DeleteWidgetFileAsync(WidgetPublisher.FileName(widget.PublicKey), ct);
        db.Widgets.Remove(widget);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Site> FindSiteAsync(Guid siteId, CancellationToken ct) =>
        await db.Sites.SingleOrDefaultAsync(s => s.Id == siteId, ct)
        ?? throw new DomainException(ErrorCodes.SiteNotFound, "Site not found");

    private static string ValidName(string? name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > Site.NameMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"Name must be 1-{Site.NameMaxLength} characters");
        return name;
    }

    /// <summary>Домен — это адрес сайта без пути: https://example.com или http://localhost:5300.</summary>
    private static List<string> ValidOrigins(IReadOnlyList<string>? origins)
    {
        var result = new List<string>();
        foreach (var raw in origins ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
                throw new DomainException(ErrorCodes.InvalidInput,
                    $"'{raw}' is not a site address. Use the form https://example.com");
            result.Add(Site.NormalizeOrigin(raw));
        }
        if (result.Count == 0)
            throw new DomainException(ErrorCodes.InvalidInput, "Add at least one domain");
        return result.Distinct().ToList();
    }

    private static SiteView ToView(Site s, IEnumerable<SiteKey> keys, int widgetCount) =>
        new(s.Id, s.Code, s.Name, s.AllowedOrigins, s.CreatedAt, keys.Select(ToView).ToList(), widgetCount);

    private static SiteKeyView ToView(SiteKey k) => new(k.Id, k.KeyId, k.CreatedAt, k.RevokedAt);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex SiteCodePattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})?$")]
    private static partial Regex LocalePattern();
}
