using System.Text.RegularExpressions;

namespace Relayo.Core.Channels;

/// <summary>Как Relayo работает с номером WhatsApp (п. 3.4 ТЗ).</summary>
public enum WhatsAppMode
{
    /// <summary>Только ссылка: виджет открывает wa.me/&lt;номер&gt;, переписка в Relayo не попадает.</summary>
    LinkOnly,

    /// <summary>
    /// Подключение по QR-коду через шлюз (src/Relayo.WhatsApp, протокол WhatsApp Web): сообщения приходят в тикеты,
    /// операторы отвечают из Relayo, переход из виджета продолжает тот же чат.
    /// </summary>
    Gateway,
}

/// <summary>
/// Канал WhatsApp: номер, на который виджет перенаправляет посетителя (кнопка WhatsApp, Relayo.open('whatsapp')),
/// а в режиме <see cref="WhatsAppMode.Gateway"/> — ещё и переписка с клиентами.
/// Клиенты, которые пишут на этот номер, относятся к сайту <see cref="SiteId"/>.
/// </summary>
public sealed partial class WhatsAppChannel
{
    public const int NameMaxLength = 200;
    public const int PhoneMaxLength = 15;
    public const int AccountNameMaxLength = 200;

    private WhatsAppChannel()
    {
    }

    public WhatsAppChannel(string name, WhatsAppMode mode, string? phoneNumber, Guid siteId, bool enabled, DateTimeOffset now)
    {
        CreatedAt = now;
        Update(name, mode, phoneNumber, siteId, enabled);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    public WhatsAppMode Mode { get; private set; }

    /// <summary>
    /// Номер в международном формате, только цифры без «+»: из него собирается ссылка wa.me/&lt;номер&gt;.
    /// В режиме «только ссылка» его вводит администратор, в режиме QR — он приходит из WhatsApp при подключении.
    /// </summary>
    public string? PhoneNumber { get; private set; }

    public Guid SiteId { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    /// <summary>Номер подключён через шлюз и принимает сообщения (последнее, что сообщил шлюз).</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Имя аккаунта WhatsApp, к которому привязан канал.</summary>
    public string? AccountName { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>id канала Usedesk (company_email_channels.id), из которого перенесён канал: повторный импорт его не дублирует,
    /// перенесённая история находит по нему канал.</summary>
    public int? UsedeskChannelId { get; private set; }

    public void LinkUsedeskChannel(int channelId) => UsedeskChannelId = channelId;

    /// <summary>Номер, на который виджет может вести посетителя: есть и канал включён.</summary>
    public bool CanRedirect => IsEnabled && PhoneNumber is not null;

    public void Update(string name, WhatsAppMode mode, string? phoneNumber, Guid siteId, bool enabled)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > NameMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"Name must be 1-{NameMaxLength} characters");
        if (!Enum.IsDefined(mode))
            throw new DomainException(ErrorCodes.InvalidInput, "Unknown WhatsApp mode");
        if (Mode == WhatsAppMode.Gateway && mode != WhatsAppMode.Gateway && IsConnected)
            throw new DomainException(ErrorCodes.InvalidInput, "Disconnect the number before switching to the link-only mode");

        Name = name;
        Mode = mode;
        // В режиме QR номер задаёт подключение, введённый вручную не нужен.
        if (mode == WhatsAppMode.LinkOnly)
            PhoneNumber = NormalizePhone(phoneNumber);
        else if (!IsConnected)
            PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? PhoneNumber : NormalizePhone(phoneNumber);
        SiteId = siteId;
        IsEnabled = enabled;
    }

    /// <summary>Шлюз сообщил, что номер подключён: номер и имя берутся из аккаунта WhatsApp.</summary>
    public void OnConnected(string? phone, string? accountName, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(phone))
            PhoneNumber = NormalizePhone(phone);
        AccountName = accountName?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, AccountNameMaxLength)] : AccountName;
        if (!IsConnected)
            ConnectedAt = now;
        IsConnected = true;
    }

    /// <summary>Номер отключён (кнопкой или из телефона). Номер остаётся: ссылка wa.me продолжает работать.</summary>
    public void OnDisconnected()
    {
        IsConnected = false;
        ConnectedAt = null;
    }

    /// <summary>
    /// «+7 (900) 123-45-67» → «79001234567». WhatsApp принимает номер с кодом страны: 8-15 цифр (E.164), первая не 0.
    /// </summary>
    public static string NormalizePhone(string? phone)
    {
        var raw = phone?.Trim() ?? string.Empty;
        if (!PhoneCharsPattern().IsMatch(raw))
            throw InvalidPhone();
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is < 8 or > PhoneMaxLength || digits[0] == '0')
            throw InvalidPhone();
        return digits;
    }

    private static DomainException InvalidPhone() =>
        new(ErrorCodes.InvalidInput, "Enter the phone number with the country code, for example +7 900 123-45-67");

    [GeneratedRegex(@"^\+?[0-9 ()\-]+$")]
    private static partial Regex PhoneCharsPattern();
}

/// <summary>
/// Код перехода из виджета в WhatsApp (п. 3.3 ТЗ, шаг 6): виджет открывает wa.me/&lt;номер&gt;?text=My code: XXXXXXXX,
/// и по коду в первом сообщении Relayo продолжает переписку посетителя. Хранится только SHA-256 кода.
/// </summary>
public sealed class WhatsAppLinkCode
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private WhatsAppLinkCode()
    {
    }

    public WhatsAppLinkCode(string codeHash, Guid channelId, Guid visitorIdentityId, DateTimeOffset now)
    {
        CodeHash = codeHash;
        ChannelId = channelId;
        VisitorIdentityId = visitorIdentityId;
        CreatedAt = now;
        ExpiresAt = now + Lifetime;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string CodeHash { get; private set; } = string.Empty;

    public Guid ChannelId { get; private set; }

    public Guid VisitorIdentityId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public void MarkUsed(DateTimeOffset now) => UsedAt ??= now;
}
