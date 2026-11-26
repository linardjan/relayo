namespace Relayo.Core.Sites;

/// <summary>
/// Сайт (product). Клиента определяет пара (сайт, clientId).
/// У сайта может быть несколько доменов: основной и зеркала (п. 3.3 ТЗ).
/// </summary>
public sealed class Site
{
    public const int CodeMaxLength = 64;
    public const int NameMaxLength = 200;

    private Site()
    {
    }

    public Site(Guid id, string code, string name, IEnumerable<string> allowedOrigins, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Id = id;
        Code = code.Trim();
        CreatedAt = now;
        Update(name, allowedOrigins);
    }

    public Guid Id { get; private set; }

    /// <summary>Код product, который передаёт сайт.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Origin основного домена и зеркал: scheme://host[:port], в нижнем регистре.</summary>
    public List<string> AllowedOrigins { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public void Update(string name, IEnumerable<string> allowedOrigins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        AllowedOrigins = allowedOrigins.Select(NormalizeOrigin).Distinct().ToList();
    }

    /// <summary>Приводит origin к виду, в котором его присылает браузер: http://localhost:5300.</summary>
    public static string NormalizeOrigin(string origin)
    {
        var uri = new Uri(origin.Trim(), UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}

/// <summary>
/// Ключ, которым backend сайта подписывает токен клиента (п. 3.3.1 ТЗ).
/// Секрет хранится зашифрованным. У сайта может быть несколько активных ключей,
/// чтобы менять ключ без простоя.
/// </summary>
public sealed class SiteKey
{
    public const int KeyIdMaxLength = 64;

    private SiteKey()
    {
    }

    public SiteKey(Guid siteId, string keyId, string protectedSecret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        SiteId = siteId;
        KeyId = keyId.Trim();
        ProtectedSecret = protectedSecret;
        CreatedAt = now;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid SiteId { get; private set; }

    public Site Site { get; private set; } = null!;

    /// <summary>Идентификатор ключа: приходит в заголовке kid токена.</summary>
    public string KeyId { get; private set; } = string.Empty;

    public string ProtectedSecret { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public void ReplaceSecret(string protectedSecret) => ProtectedSecret = protectedSecret;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public enum IdentificationMode
{
    /// <summary>Сайт передаёт подписанный токен: Relayo.identify(token).</summary>
    SignedToken,

    /// <summary>Сайт передаёт clientId и product как есть (небезопасный режим, п. 3.3.2 ТЗ).</summary>
    Direct,
}

public enum WidgetPosition
{
    Right,
    Left,
}

/// <summary>Виджет чата на сайте. Конструктор виджетов (п. 3.5 ТЗ) появится на этапе 4.</summary>
public sealed class Widget
{
    private Widget()
    {
    }

    public Widget(Guid id, Guid siteId, string name, IdentificationMode mode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        SiteId = siteId;
        Name = name.Trim();
        IdentificationMode = mode;
        CreatedAt = now;
        UpdatedAt = now;
        PublicKey = WidgetKeys.Create();
    }

    public const int PublicKeyLength = 10;

    /// <summary>
    /// Публичный ключ: из него собирается имя файла виджета widget_&lt;ключ&gt;.js в S3.
    /// Случайный, поэтому виджеты нельзя перебрать по номерам.
    /// </summary>
    public string PublicKey { get; private set; } = string.Empty;

    /// <summary>Последняя опубликованная версия. Пусто — виджет ещё не публиковали.</summary>
    public int? PublishedVersion { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Когда меняли настройки. Позже публикации — на сайтах ещё старая версия.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasUnpublishedChanges => PublishedAt is null || UpdatedAt > PublishedAt;

    public void MarkPublished(int version, DateTimeOffset now)
    {
        PublishedVersion = version;
        PublishedAt = now;
    }

    /// <summary>Только для тестового сайта в Development: ключ задан в .env, чтобы TestSite знал имя файла.</summary>
    internal void SetPublicKey(string key) => PublicKey = key;

    public Guid Id { get; private set; }

    public Guid SiteId { get; private set; }

    public Site Site { get; private set; } = null!;

    public string Name { get; private set; } = string.Empty;

    public IdentificationMode IdentificationMode { get; private set; }

    public string Title { get; private set; } = "Support";

    public string Greeting { get; private set; } = "Hi! How can we help you?";

    public string AccentColor { get; private set; } = "#1677ff";

    public WidgetPosition Position { get; private set; } = WidgetPosition.Right;

    public string DefaultLocale { get; private set; } = "en";

    /// <summary>Какого Telegram-бота открывает кнопка «Continue in Telegram». Пусто — кнопки нет.</summary>
    public Guid? TelegramBotId { get; private set; }

    /// <summary>На какой номер WhatsApp ведёт кнопка WhatsApp. Пусто — кнопки нет.</summary>
    public Guid? WhatsAppChannelId { get; private set; }

    /// <summary>Внешний вид и поведение на сайте: подпись, кнопка, подсказка, звук, пункты.</summary>
    public WidgetOptions Options { get; private set; } = WidgetOptions.Default;

    public DateTimeOffset CreatedAt { get; private set; }

    public void Configure(string name, IdentificationMode mode, string title, string greeting, string accentColor,
        WidgetPosition position, string defaultLocale, Guid? telegramBotId, Guid? whatsAppChannelId, WidgetOptions options,
        DateTimeOffset now)
    {
        var changed = Name != name || IdentificationMode != mode || Title != title || Greeting != greeting
                      || AccentColor != accentColor || Position != position || DefaultLocale != defaultLocale
                      || TelegramBotId != telegramBotId || WhatsAppChannelId != whatsAppChannelId
                      || !SameOptions(Options, options);
        if (changed)
            UpdatedAt = now;
        Options = options;
        TelegramBotId = telegramBotId;
        WhatsAppChannelId = whatsAppChannelId;
        Name = name;
        IdentificationMode = mode;
        Title = title;
        Greeting = greeting;
        AccentColor = accentColor;
        Position = position;
        DefaultLocale = defaultLocale;
    }

    /// <summary>Бот удалён: кнопка Telegram пропадает из виджета.</summary>
    public void DetachTelegramBot(DateTimeOffset now)
    {
        TelegramBotId = null;
        Options = Options with { Items = Options.Items.Where(i => i.Type != LauncherItemType.Telegram).ToList() };
        UpdatedAt = now;
    }

    /// <summary>Канал WhatsApp удалён: кнопка WhatsApp пропадает из виджета.</summary>
    public void DetachWhatsAppChannel(DateTimeOffset now)
    {
        WhatsAppChannelId = null;
        Options = Options with { Items = Options.Items.Where(i => i.Type != LauncherItemType.WhatsApp).ToList() };
        UpdatedAt = now;
    }

    /// <summary>Своя картинка кнопки загружена или убрана: на сайтах она появится после публикации.</summary>
    public void SetButtonIcon(string? fileName, DateTimeOffset now)
    {
        Options = Options with { ButtonIcon = fileName };
        UpdatedAt = now;
    }

    // В настройках есть списки: записи с ними сравниваются по ссылке, поэтому сравниваем содержимое.
    private static bool SameOptions(WidgetOptions a, WidgetOptions b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);
}

/// <summary>Публичные ключи виджетов: 10 символов из латиницы и цифр, около 59 бит случайности.</summary>
public static class WidgetKeys
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Create() =>
        string.Create(Widget.PublicKeyLength, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });

    public static bool IsValid(string? key) =>
        key is { Length: Widget.PublicKeyLength } && key.All(c => char.IsAsciiLetterOrDigit(c));
}
