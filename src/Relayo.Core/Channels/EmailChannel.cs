using System.Net.Mail;

namespace Relayo.Core.Channels;

/// <summary>Шифрование соединения с почтовым сервером.</summary>
public enum MailSecurity
{
    /// <summary>TLS сразу при подключении: IMAP 993, SMTP 465.</summary>
    Ssl,

    /// <summary>Обычное подключение, затем STARTTLS: IMAP 143, SMTP 587.</summary>
    StartTls,

    /// <summary>Без шифрования: пароль идёт открытым текстом. Только для внутренней сети.</summary>
    None,
}

/// <summary>
/// Подключение к почтовому серверу (входящее IMAP или исходящее SMTP). Пароль — зашифрованный <c>SecretProtector</c>.
/// </summary>
/// <param name="Username">Имя пользователя для входа, если отличается от адреса; null — входить по адресу.</param>
public sealed record MailServer(string Email, string Host, int Port, MailSecurity Security, string? Username,
    string ProtectedPassword)
{
    public const int EmailMaxLength = 320;
    public const int HostMaxLength = 255;
    public const int UsernameMaxLength = 320;
    public const int ProtectedPasswordMaxLength = 1024;

    /// <summary>Под каким именем входить на сервер.</summary>
    public string Login => Username ?? Email;

    /// <summary>Проверяет и приводит поля к виду для хранения. <paramref name="side"/> — «Incoming» / «Outgoing» для текста ошибки.</summary>
    public static MailServer Create(string side, string? email, string? host, int port, MailSecurity security,
        string? username, string protectedPassword)
    {
        email = email?.Trim() ?? string.Empty;
        if (email.Length is 0 or > EmailMaxLength || !MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new DomainException(ErrorCodes.InvalidInput, $"{side} connection: enter a valid email address");

        host = host?.Trim().TrimEnd('.') ?? string.Empty;
        if (host.Length is 0 or > HostMaxLength || host.Contains("://") || host.Any(c => c is '/' or ':' or '@' || char.IsWhiteSpace(c)))
            throw new DomainException(ErrorCodes.InvalidInput,
                $"{side} connection: the server is a host name or IP address without protocol and port");

        if (port is < 1 or > 65535)
            throw new DomainException(ErrorCodes.InvalidInput, $"{side} connection: port must be 1-65535");
        if (!Enum.IsDefined(security))
            throw new DomainException(ErrorCodes.InvalidInput, $"{side} connection: unknown encryption");

        username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (username is { Length: > UsernameMaxLength })
            throw new DomainException(ErrorCodes.InvalidInput, $"{side} connection: username is too long");
        // Совпадает с адресом — не храним отдельно: при смене адреса имя меняется вместе с ним.
        if (string.Equals(username, email, StringComparison.OrdinalIgnoreCase))
            username = null;

        ArgumentException.ThrowIfNullOrEmpty(protectedPassword);
        return new MailServer(email, host.ToLowerInvariant(), port, security, username, protectedPassword);
    }
}

/// <summary>Отправлять ли клиенту в ответе цитату его письма и предыдущей переписки.</summary>
public enum EmailQuoting
{
    SendToClient,
    DoNotSend,
}

/// <summary>
/// Почтовый канал: ящик, письма из которого становятся тикетами (IMAP), и сервер, через который уходят ответы (SMTP).
/// Клиенты, которые пишут на этот ящик, относятся к сайту <see cref="SiteId"/>.
/// Пока Relayo хранит настройки и проверяет подключение; приём и отправка писем — следующий шаг.
/// </summary>
public sealed class EmailChannel
{
    public const int NameMaxLength = 200;
    public const int CheckMessageMaxLength = 1000;

    private EmailChannel()
    {
    }

    public EmailChannel(string name, Guid siteId, bool enabled, bool forwardCreatesTicket, EmailQuoting quoting,
        MailServer imap, MailServer smtp, DateTimeOffset now)
    {
        CreatedAt = now;
        Update(name, siteId, enabled, forwardCreatesTicket, quoting);
        SetServers(imap, smtp);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    public Guid SiteId { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    /// <summary>Пересланное клиентом письмо (Fwd:) создаёт новый тикет, а не продолжает прежний.</summary>
    public bool ForwardCreatesTicket { get; private set; }

    public EmailQuoting Quoting { get; private set; }

    /// <summary>Входящее соединение: ящик, который читает Relayo.</summary>
    public MailServer Imap { get; private set; } = null!;

    /// <summary>Исходящее соединение: через него уходят ответы операторов.</summary>
    public MailServer Smtp { get; private set; } = null!;

    public DateTimeOffset? LastCheckAt { get; private set; }

    public bool? LastCheckOk { get; private set; }

    public string? LastCheckMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>id канала Usedesk (company_email_channels.id), из которого перенесён канал: повторный импорт его не дублирует,
    /// перенесённая история находит по нему канал.</summary>
    public int? UsedeskChannelId { get; private set; }

    public void LinkUsedeskChannel(int channelId) => UsedeskChannelId = channelId;

    public void Update(string name, Guid siteId, bool enabled, bool forwardCreatesTicket, EmailQuoting quoting)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > NameMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"Name must be 1-{NameMaxLength} characters");
        if (!Enum.IsDefined(quoting))
            throw new DomainException(ErrorCodes.InvalidInput, "Unknown quoting mode");

        Name = name;
        SiteId = siteId;
        IsEnabled = enabled;
        ForwardCreatesTicket = forwardCreatesTicket;
        Quoting = quoting;
    }

    /// <summary>Меняет подключения. Если они поменялись, прежний результат проверки больше не о них — сбрасывается.</summary>
    public void SetServers(MailServer imap, MailServer smtp)
    {
        ArgumentNullException.ThrowIfNull(imap);
        ArgumentNullException.ThrowIfNull(smtp);
        if (imap == Imap && smtp == Smtp)
            return;
        Imap = imap;
        Smtp = smtp;
        LastCheckAt = null;
        LastCheckOk = null;
        LastCheckMessage = null;
    }

    public void RecordCheck(bool ok, string message, DateTimeOffset now)
    {
        LastCheckAt = now;
        LastCheckOk = ok;
        LastCheckMessage = message.Length > CheckMessageMaxLength ? message[..CheckMessageMaxLength] : message;
    }
}
