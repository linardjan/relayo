using System.Security.Authentication;
using System.Text;
using Novell.Directory.Ldap;

namespace Relayo.Core.Directory;

/// <summary>Пользователь из папки AD.</summary>
public sealed record DirectoryUser(Guid ObjectGuid, string Login, string FullName, bool IsDisabled,
    string? Email = null, string? Phone = null, string? Position = null);

/// <summary>Ошибка LDAP с понятным администратору текстом. Пароли и содержимое ответов в текст не попадают.</summary>
public sealed class DirectoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Работа с AD по LDAP (Novell.Directory.Ldap: управляемый код, одинаково работает в Windows, Linux и macOS,
/// проверка сертификата настраивается в коде, а не переменными окружения libldap).
/// </summary>
public sealed class LdapDirectory
{
    private const int TimeoutMs = 10_000;
    private const int PageSize = 500;

    /// <summary>Подключается служебной учёткой и считает пользователей папки.</summary>
    public async Task<int> CheckAsync(LdapSettings settings, string? bindPassword, CancellationToken ct) =>
        (await FetchUsersAsync(settings, bindPassword, ct)).Count;

    /// <summary>Пользователи папки (без вложенных папок).</summary>
    public async Task<IReadOnlyList<DirectoryUser>> FetchUsersAsync(LdapSettings settings, string? bindPassword,
        CancellationToken ct)
    {
        using var connection = await ConnectAsync(settings, ct);
        await BindAsync(connection, settings.BindDn, bindPassword ?? string.Empty, serviceAccount: true, ct);

        var filter = settings.Provider == DirectoryProvider.ActiveDirectory
            ? "(&(objectCategory=person)(objectClass=user))"
            : "(objectClass=person)";
        var attributes = new[] { settings.GuidAttribute, settings.LoginAttribute, settings.FullNameAttribute, "userAccountControl",
                settings.MailAttribute, settings.PhoneAttribute, settings.PositionAttribute }
            .OfType<string>().Where(a => a.Length > 0).Distinct().ToArray();
        List<LdapEntry> entries;
        try
        {
            // Постранично: AD по умолчанию отдаёт не больше 1000 записей за один запрос.
            entries = await connection.SearchUsingSimplePagingAsync(
                new SearchOptions(settings.UsersDn, LdapConnection.ScopeOne, filter, attributes), PageSize, ct);
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.NoSuchObject)
        {
            throw new DirectoryException($"Users folder “{settings.UsersDn}” was not found", ex);
        }
        catch (LdapException ex)
        {
            throw new DirectoryException($"Search failed: {ex.ResultCodeToString()}", ex);
        }

        var users = new List<DirectoryUser>();
        foreach (var entry in entries)
        {
            var login = entry.GetStringValueOrDefault(settings.LoginAttribute, null);
            var guid = ReadGuid(entry, settings.GuidAttribute);
            if (string.IsNullOrWhiteSpace(login) || guid is null)
                continue;
            var fullName = entry.GetStringValueOrDefault(settings.FullNameAttribute, null) ?? login;
            // В AD отключённая учётка помечается битом 0x2 в userAccountControl.
            var disabled = int.TryParse(entry.GetStringValueOrDefault("userAccountControl", null), out var uac)
                           && (uac & 0x2) != 0;
            string? Optional(string? attribute) =>
                string.IsNullOrEmpty(attribute) ? null : entry.GetStringValueOrDefault(attribute, null);
            users.Add(new DirectoryUser(guid.Value, login, fullName, disabled,
                Optional(settings.MailAttribute), Optional(settings.PhoneAttribute), Optional(settings.PositionAttribute)));
        }
        return users;
    }

    /// <summary>
    /// Проверяет пароль пользователя: подключается к AD от его имени (п. 3.1 ТЗ). Пароль в Relayo не хранится.
    /// Возвращает objectGUID записи, чтобы убедиться, что это тот же человек, которого импортировали.
    /// </summary>
    public async Task<Guid?> AuthenticateAsync(LdapSettings settings, string login, string password, CancellationToken ct)
    {
        var dn = $"{settings.LoginAttribute}={EscapeDnValue(login)},{settings.UsersDn}";
        using var connection = await ConnectAsync(settings, ct);
        try
        {
            await connection.BindAsync(dn, password, ct);
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidCredentials)
        {
            return null;
        }
        catch (LdapException ex)
        {
            throw new DirectoryException($"Sign-in check failed: {ex.ResultCodeToString()}", ex);
        }

        var results = await connection.SearchAsync(dn, LdapConnection.ScopeBase, "(objectClass=*)",
            [settings.GuidAttribute], false, ct);
        while (await results.HasMoreAsync(ct))
        {
            var entry = await results.NextAsync(ct);
            return ReadGuid(entry, settings.GuidAttribute);
        }
        return null;
    }

    private static async Task<LdapConnection> ConnectAsync(LdapSettings settings, CancellationToken ct)
    {
        var options = new LdapConnectionOptions().ConfigureSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
        if (settings.Security == LdapSecurity.Ldaps)
            options = options.UseSsl();
        if (!settings.VerifyCertificate)
            options = options.ConfigureRemoteCertificateValidationCallback((_, _, _, _) => true);

        var connection = new LdapConnection(options) { ConnectionTimeout = TimeoutMs };
        connection.Constraints = new LdapConstraints { TimeLimit = TimeoutMs };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);
            await connection.ConnectAsync(settings.Host, settings.Port, timeout.Token);
            if (settings.Security == LdapSecurity.StartTls)
                await connection.StartTlsAsync(timeout.Token);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            connection.Dispose();
            throw new DirectoryException(Describe(ex, settings), ex);
        }
    }

    private static async Task BindAsync(LdapConnection connection, string dn, string password, bool serviceAccount,
        CancellationToken ct)
    {
        try
        {
            await connection.BindAsync(dn, password, ct);
        }
        catch (LdapException ex) when (ex.ResultCode == LdapException.InvalidCredentials)
        {
            throw new DirectoryException(serviceAccount
                ? "The service account DN or password is wrong"
                : "Wrong login or password", ex);
        }
        catch (LdapException ex)
        {
            throw new DirectoryException($"Bind failed: {ex.ResultCodeToString()}", ex);
        }
    }

    private static string Describe(Exception ex, LdapSettings settings)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return settings.VerifyCertificate
                    ? "TLS failed: the server certificate is not trusted. Install the CA certificate on the Relayo server "
                      + "(turn off certificate verification only for a test AD)"
                    : "TLS handshake failed. Check the port and the encryption type";
        }
        return ex is OperationCanceledException
            ? $"No answer from {settings.Host}:{settings.Port} within {TimeoutMs / 1000} seconds"
            : $"Cannot connect to {settings.Host}:{settings.Port}. Check the host, the port and the encryption type";
    }

    private static Guid? ReadGuid(LdapEntry entry, string attribute)
    {
        if (!entry.TryGet(attribute, out var value) || value is null)
            return null;
        // objectGUID в AD — 16 байт; entryUUID в OpenLDAP — строка.
        var bytes = value.ByteValue;
        if (bytes is { Length: 16 })
            return new Guid(bytes);
        return Guid.TryParse(value.StringValue, out var guid) ? guid : null;
    }

    /// <summary>Экранирование значения в DN (RFC 4514): логин не должен менять путь к записи.</summary>
    internal static string EscapeDnValue(string value)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var special = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                          || (i == 0 && (c == '#' || c == ' '))
                          || (i == value.Length - 1 && c == ' ');
            if (c < 0x20)
                sb.Append('\\').Append(((int)c).ToString("x2"));
            else
            {
                if (special)
                    sb.Append('\\');
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
