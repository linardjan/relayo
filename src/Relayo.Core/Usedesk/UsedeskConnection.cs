namespace Relayo.Core.Usedesk;

/// <summary>
/// Подключение к базе Usedesk (MySQL) для временного импорта каналов, виджетов и пользователей (п. 3.8 ТЗ).
/// Одна запись. Пароль — зашифрованный <c>SecretProtector</c>; после переезда подключение удаляют кнопкой «Forget».
/// </summary>
public sealed class UsedeskConnection
{
    public const int HostMaxLength = 255;
    public const int DatabaseMaxLength = 64;
    public const int UsernameMaxLength = 128;
    public const int CheckMessageMaxLength = 1000;

    private UsedeskConnection()
    {
    }

    public UsedeskConnection(DateTimeOffset now) => UpdatedAt = now;

    public int Id { get; private set; } = 1;

    public string Host { get; private set; } = string.Empty;

    public int Port { get; private set; } = 3306;

    public string Database { get; private set; } = string.Empty;

    public string Username { get; private set; } = string.Empty;

    public string? ProtectedPassword { get; private set; }

    /// <summary>Наша компания в Usedesk: в его таблицах лежат данные всех компаний экземпляра.</summary>
    public int CompanyId { get; private set; }

    public DateTimeOffset? LastCheckAt { get; private set; }

    public bool? LastCheckOk { get; private set; }

    public string? LastCheckMessage { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsConfigured => Host.Length > 0 && ProtectedPassword is not null && CompanyId > 0;

    public void Configure(string? host, int port, string? database, string? username, int companyId, DateTimeOffset now)
    {
        host = host?.Trim() ?? string.Empty;
        database = database?.Trim() ?? string.Empty;
        username = username?.Trim() ?? string.Empty;
        if (host.Length is 0 or > HostMaxLength || host.Contains("://") || host.Any(c => c is '/' or ';' || char.IsWhiteSpace(c)))
            throw Invalid("Server is a host name or IP address without protocol");
        if (port is < 1 or > 65535)
            throw Invalid("Port must be 1-65535");
        if (database.Length is 0 or > DatabaseMaxLength || database.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '$')))
            throw Invalid("Database name may contain only Latin letters, digits and _");
        if (username.Length is 0 or > UsernameMaxLength || username.Contains(';'))
            throw Invalid("Username is required");
        if (companyId <= 0)
            throw Invalid("Company id is a positive number: company_id in the Usedesk tables");

        var changed = (Host, Port, Database, Username, CompanyId) != (host, port, database, username, companyId);
        Host = host;
        Port = port;
        Database = database;
        Username = username;
        CompanyId = companyId;
        UpdatedAt = now;
        if (changed)
            ResetCheck();
    }

    public void SetPassword(string protectedPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedPassword);
        ProtectedPassword = protectedPassword;
        ResetCheck();
    }

    public void RecordCheck(bool ok, string message, DateTimeOffset now)
    {
        LastCheckAt = now;
        LastCheckOk = ok;
        LastCheckMessage = message.Length > CheckMessageMaxLength ? message[..CheckMessageMaxLength] : message;
    }

    private void ResetCheck()
    {
        LastCheckAt = null;
        LastCheckOk = null;
        LastCheckMessage = null;
    }

    private static DomainException Invalid(string message) => new(ErrorCodes.InvalidInput, message);
}
