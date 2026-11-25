using Relayo.Core.Users;

namespace Relayo.Core.Directory;

/// <summary>Как защищено подключение. Без шифрования пароль пошёл бы по сети открытым текстом, поэтому такого варианта нет.</summary>
public enum LdapSecurity
{
    /// <summary>TLS с самого начала, обычно порт 636.</summary>
    Ldaps,

    /// <summary>Обычное подключение, затем StartTLS, обычно порт 389.</summary>
    StartTls,
}

public enum DirectoryProvider
{
    ActiveDirectory,
    OpenLdap,
}

/// <summary>
/// Подключение к AD (п. 3.1 ТЗ). Одна запись на всю систему, настраивается в админке («Settings → LDAP»).
/// Пароль служебной учётки хранится зашифрованным и наружу не отдаётся.
/// </summary>
public sealed class LdapSettings
{
    public const int SingletonId = 1;
    public const int TextMaxLength = 500;
    public const int AttributeMaxLength = 100;

    public int Id { get; private set; } = SingletonId;

    /// <summary>Вход по учётке AD и синхронизация по расписанию включены.</summary>
    public bool IsEnabled { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public int Port { get; private set; } = 636;

    public LdapSecurity Security { get; private set; } = LdapSecurity.Ldaps;

    /// <summary>Проверять сертификат сервера. Выключать только для тестового AD с самоподписанным сертификатом.</summary>
    public bool VerifyCertificate { get; private set; } = true;

    public DirectoryProvider Provider { get; private set; } = DirectoryProvider.ActiveDirectory;

    /// <summary>Служебная учётка с правами только на чтение: ею читается папка при синхронизации.</summary>
    public string BindDn { get; private set; } = string.Empty;

    public string? ProtectedBindPassword { get; private set; }

    /// <summary>
    /// Папка (OU), из которой импортируются пользователи, без вложенных папок. Внутри неё CN уникален,
    /// поэтому при входе Relayo подключается как &lt;атрибут логина&gt;=&lt;логин&gt;,&lt;эта папка&gt;.
    /// </summary>
    public string UsersDn { get; private set; } = string.Empty;

    public string GuidAttribute { get; private set; } = "objectGUID";

    public string LoginAttribute { get; private set; } = "cn";

    public string FullNameAttribute { get; private set; } = "displayName";

    /// <summary>Необязательные атрибуты: почта, телефон и должность сотрудника. Пусто — не брать из AD.</summary>
    public string? MailAttribute { get; private set; } = "mail";

    public string? PhoneAttribute { get; private set; } = "telephoneNumber";

    public string? PositionAttribute { get; private set; } = "title";

    /// <summary>Роль новых пользователей из AD. Потом её можно поменять каждому в разделе Agents.</summary>
    public UserRole DefaultRole { get; private set; } = UserRole.Operator;

    /// <summary>Как часто синхронизировать папку. 0 — только вручную, кнопкой «Import users».</summary>
    public int SyncIntervalMinutes { get; private set; } = 60;

    public DateTimeOffset? LastCheckAt { get; private set; }

    public bool? LastCheckOk { get; private set; }

    public string? LastCheckMessage { get; private set; }

    public DateTimeOffset? LastSyncAt { get; private set; }

    public bool? LastSyncOk { get; private set; }

    public string? LastSyncMessage { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsConfigured => Host.Length > 0 && UsersDn.Length > 0;

    public void Configure(bool enabled, string host, int port, LdapSecurity security, bool verifyCertificate,
        DirectoryProvider provider, string bindDn, string usersDn, string guidAttribute, string loginAttribute,
        string fullNameAttribute, UserRole defaultRole, int syncIntervalMinutes, DateTimeOffset now)
    {
        IsEnabled = enabled;
        Host = host;
        Port = port;
        Security = security;
        VerifyCertificate = verifyCertificate;
        Provider = provider;
        BindDn = bindDn;
        UsersDn = usersDn;
        GuidAttribute = guidAttribute;
        LoginAttribute = loginAttribute;
        FullNameAttribute = fullNameAttribute;
        DefaultRole = defaultRole;
        SyncIntervalMinutes = syncIntervalMinutes;
        UpdatedAt = now;
    }

    public void SetProfileAttributes(string? mail, string? phone, string? position)
    {
        MailAttribute = mail;
        PhoneAttribute = phone;
        PositionAttribute = position;
    }

    public void SetBindPassword(string protectedPassword) => ProtectedBindPassword = protectedPassword;

    public void RecordCheck(bool ok, string message, DateTimeOffset now)
    {
        LastCheckAt = now;
        LastCheckOk = ok;
        LastCheckMessage = message;
    }

    public void RecordSync(bool ok, string message, DateTimeOffset now)
    {
        LastSyncAt = now;
        LastSyncOk = ok;
        LastSyncMessage = message;
    }
}
