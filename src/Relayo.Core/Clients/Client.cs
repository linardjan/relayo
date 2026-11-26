namespace Relayo.Core.Clients;

/// <summary>
/// Клиент. Пока посетитель не авторизовался на сайте, это анонимный клиент без clientId.
/// После идентификации клиента определяет пара (сайт, clientId).
/// </summary>
public sealed class Client
{
    public const int ExternalIdMaxLength = 128;
    public const int DisplayNameMaxLength = 200;

    private Client()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid SiteId { get; private set; }

    /// <summary>clientId с сайта. Пусто у анонимного клиента.</summary>
    public string? ExternalClientId { get; private set; }

    public string? Locale { get; private set; }

    /// <summary>Имя из мессенджера (например, из профиля Telegram): по нему оператор узнаёт анонимного клиента.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// clientId подтверждён подписанным токеном. Если клиент известен только по прямой
    /// передаче clientId, оператор видит пометку «clientId не подтверждён» (п. 3.3.2 ТЗ).
    /// </summary>
    public bool IsVerified { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>id клиента в Usedesk: к нему привязываются перенесённые тикеты и чаты.</summary>
    public long? UsedeskId { get; private set; }

    public bool IsAnonymous => ExternalClientId is null;

    public static Client CreateAnonymous(Guid siteId, DateTimeOffset now) =>
        new() { SiteId = siteId, CreatedAt = now };

    public static Client CreateIdentified(Guid siteId, string externalClientId, DateTimeOffset now) =>
        new() { SiteId = siteId, ExternalClientId = externalClientId, CreatedAt = now };

    public void MarkVerified() => IsVerified = true;

    public void SetLocale(string? locale)
    {
        if (!string.IsNullOrWhiteSpace(locale))
            Locale = locale.Trim();
    }

    public void SetLocaleIfEmpty(string? locale)
    {
        if (Locale is null)
            SetLocale(locale);
    }

    public void SetDisplayNameIfEmpty(string? name)
    {
        if (DisplayName is null && !string.IsNullOrWhiteSpace(name))
            DisplayName = name.Trim() is var n && n.Length > DisplayNameMaxLength ? n[..DisplayNameMaxLength] : n;
    }
}

public enum IdentityType
{
    /// <summary>Браузер посетителя: токен виджета в localStorage.</summary>
    Visitor,
    Telegram,
    WhatsApp,
}

/// <summary>
/// Канал, через который клиент пишет: браузер с виджетом, аккаунт Telegram, номер WhatsApp.
/// У одного клиента может быть несколько идентичностей.
/// </summary>
public sealed class ClientIdentity
{
    public const int ExternalIdMaxLength = 128;
    public const int DisplayNameMaxLength = 200;

    private ClientIdentity()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    /// <summary>Пусто, пока посетитель ничего не написал и не прошёл идентификацию.</summary>
    public Guid? ClientId { get; private set; }

    public Client? Client { get; private set; }

    public IdentityType Type { get; private set; }

    /// <summary>
    /// Для посетителя это SHA-256 от его токена: сам токен знает только браузер.
    /// Для Telegram — «id бота в Relayo:id чата», потому что у каждого бота своя переписка с человеком.
    /// </summary>
    public string ExternalId { get; private set; } = string.Empty;

    /// <summary>Подключение канала, через которое пишет клиент: Telegram-бот или номер WhatsApp.</summary>
    public Guid? ChannelId { get; private set; }

    /// <summary>Имя в мессенджере: «Иван Петров (@ivan)». Видят только операторы.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// Посетитель виджета, из которого клиент перешёл в мессенджер. Сообщения из мессенджера
    /// продолжают его чат (п. 1 ТЗ: переход продолжает тот же чат).
    /// </summary>
    public Guid? LinkedFromIdentityId { get; private set; }

    /// <summary>Посетитель подтвердил личность подписанным токеном.</summary>
    public bool IsVerified { get; private set; }

    public Guid? WidgetId { get; private set; }

    public Guid SiteId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public static ClientIdentity CreateVisitor(Guid siteId, Guid widgetId, string tokenHash, DateTimeOffset now) => new()
    {
        Type = IdentityType.Visitor,
        SiteId = siteId,
        WidgetId = widgetId,
        ExternalId = tokenHash,
        CreatedAt = now,
        LastSeenAt = now,
    };

    public static ClientIdentity CreateTelegram(Guid botId, long chatId, Guid siteId, DateTimeOffset now) => new()
    {
        Type = IdentityType.Telegram,
        SiteId = siteId,
        ChannelId = botId,
        ExternalId = TelegramExternalId(botId, chatId),
        CreatedAt = now,
        LastSeenAt = now,
    };

    public static string TelegramExternalId(Guid botId, long chatId) => $"{botId:N}:{chatId}";

    /// <summary>Куда отправлять ответ в Telegram: бот и чат.</summary>
    public bool TryGetTelegramChat(out Guid botId, out long chatId)
    {
        botId = ChannelId ?? Guid.Empty;
        chatId = 0;
        var separator = ExternalId.IndexOf(':');
        return Type == IdentityType.Telegram && ChannelId is not null && separator > 0
               && long.TryParse(ExternalId.AsSpan(separator + 1), out chatId);
    }

    /// <param name="chatJid">Куда отвечать в WhatsApp: «номер@s.whatsapp.net» или скрытый id «…@lid».</param>
    public static ClientIdentity CreateWhatsApp(Guid channelId, string chatJid, Guid siteId, DateTimeOffset now) => new()
    {
        Type = IdentityType.WhatsApp,
        SiteId = siteId,
        ChannelId = channelId,
        ExternalId = WhatsAppExternalId(channelId, chatJid),
        CreatedAt = now,
        LastSeenAt = now,
    };

    /// <summary>У каждого номера компании своя переписка с человеком: «id канала:JID чата».</summary>
    public static string WhatsAppExternalId(Guid channelId, string chatJid) => $"{channelId:N}:{chatJid}";

    /// <summary>Куда отправлять ответ в WhatsApp: канал (номер компании) и чат клиента.</summary>
    public bool TryGetWhatsAppChat(out Guid channelId, out string chatJid)
    {
        channelId = ChannelId ?? Guid.Empty;
        chatJid = string.Empty;
        var separator = ExternalId.IndexOf(':');
        if (Type != IdentityType.WhatsApp || ChannelId is null || separator <= 0 || separator == ExternalId.Length - 1)
            return false;
        chatJid = ExternalId[(separator + 1)..];
        return true;
    }

    public void SetDisplayName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            DisplayName = name.Trim() is var n && n.Length > DisplayNameMaxLength ? n[..DisplayNameMaxLength] : n;
    }

    /// <summary>Клиент перешёл в мессенджер из виджета: эта идентичность продолжает переписку посетителя.</summary>
    public void LinkFrom(ClientIdentity visitor, Client client)
    {
        AttachTo(client, visitor.IsVerified);
        LinkedFromIdentityId = visitor.Id;
        SiteId = visitor.SiteId;
    }

    public void AttachTo(Client client, bool verified)
    {
        Client = client;
        ClientId = client.Id;
        IsVerified = verified;
    }

    public void MarkVerified() => IsVerified = true;

    public void Touch(DateTimeOffset now) => LastSeenAt = now;
}
