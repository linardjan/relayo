namespace Relayo.Core.Channels;

/// <summary>
/// Telegram-бот, подключённый к Relayo (п. 3.4 ТЗ). Токен бота и секрет webhook хранятся зашифрованными.
/// Клиенты, которые пишут боту напрямую, а не переходят из виджета, относятся к сайту <see cref="SiteId"/>.
/// </summary>
public sealed class TelegramBot
{
    public const int NameMaxLength = 200;
    public const int UsernameMaxLength = 64;

    private TelegramBot()
    {
    }

    public TelegramBot(string name, long telegramId, string username, Guid siteId, string protectedToken,
        string protectedWebhookSecret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        SiteId = siteId;
        ProtectedWebhookSecret = protectedWebhookSecret;
        CreatedAt = now;
        ReplaceToken(telegramId, username, protectedToken);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    /// <summary>Id бота в Telegram (число перед двоеточием в токене).</summary>
    public long TelegramId { get; private set; }

    /// <summary>Имя бота без @: из него собирается ссылка t.me/&lt;username&gt;.</summary>
    public string Username { get; private set; } = string.Empty;

    public Guid SiteId { get; private set; }

    public string ProtectedToken { get; private set; } = string.Empty;

    /// <summary>Telegram присылает его в заголовке X-Telegram-Bot-Api-Secret-Token каждого вызова webhook.</summary>
    public string ProtectedWebhookSecret { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>id канала Usedesk (company_email_channels.id), из которого перенесён канал: повторный импорт его не дублирует,
    /// перенесённая история находит по нему канал.</summary>
    public int? UsedeskChannelId { get; private set; }

    public void LinkUsedeskChannel(int channelId) => UsedeskChannelId = channelId;

    public void Update(string name, Guid siteId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        SiteId = siteId;
        IsEnabled = enabled;
    }

    public void ReplaceToken(long telegramId, string username, string protectedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        TelegramId = telegramId;
        Username = username;
        ProtectedToken = protectedToken;
    }
}

/// <summary>
/// Одноразовая ссылка для перехода из виджета в мессенджер (п. 3.3 ТЗ, шаг 5).
/// Хранится только SHA-256 токена: сам токен есть лишь в ссылке у посетителя.
/// </summary>
public sealed class ChannelLinkToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private ChannelLinkToken()
    {
    }

    public ChannelLinkToken(string tokenHash, Guid botId, Guid visitorIdentityId, DateTimeOffset now)
    {
        TokenHash = tokenHash;
        BotId = botId;
        VisitorIdentityId = visitorIdentityId;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string TokenHash { get; private set; } = string.Empty;

    public Guid BotId { get; private set; }

    /// <summary>Посетитель виджета, который запросил ссылку.</summary>
    public Guid VisitorIdentityId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void MarkUsed(DateTimeOffset now) => UsedAt ??= now;
}
