namespace Relayo.Core.Users;

public enum UserRole
{
    Operator,
    Supervisor,
    Admin,
}

public enum UserSource
{
    /// <summary>Свой логин и пароль, хеш пароля хранится в Relayo.</summary>
    Local,

    /// <summary>Импортирован из AD. Пароль проверяет AD, в Relayo он не хранится.</summary>
    ActiveDirectory,
}

/// <summary>
/// Сотрудник: администратор, супервизор или оператор.
/// ФИО и логин видят только сотрудники. Клиентам показывается только псевдоним (п. 3.1 ТЗ).
/// </summary>
public sealed class User
{
    public const int LoginMaxLength = 256;
    public const int FullNameMaxLength = 256;
    public const int AliasMaxLength = 100;

    private User()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    /// <summary>Логин для входа. Для пользователей из AD это CN.</summary>
    public string Login { get; private set; } = string.Empty;

    /// <summary>Логин в верхнем регистре: по нему ищем при входе и проверяем уникальность.</summary>
    public string LoginNormalized { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    /// <summary>Псевдоним для клиентов. Пока он не задан, оператор не может брать чаты.</summary>
    public string? Alias { get; private set; }

    public UserRole Role { get; private set; }

    public UserSource Source { get; private set; }

    /// <summary>objectGUID из AD: по нему пользователь сопоставляется при синхронизации.</summary>
    public Guid? AdObjectGuid { get; private set; }

    /// <summary>Хеш пароля, только у локальных пользователей.</summary>
    public string? PasswordHash { get; private set; }

    public bool IsBlocked { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static User CreateLocal(string login, string fullName, UserRole role, string? alias, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        return new User
        {
            Login = login.Trim(),
            LoginNormalized = NormalizeLogin(login),
            FullName = fullName.Trim(),
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
            Role = role,
            Source = UserSource.Local,
            CreatedAt = now,
        };
    }

    public static string NormalizeLogin(string login) => login.Trim().ToUpperInvariant();

    public void SetPasswordHash(string passwordHash)
    {
        if (Source != UserSource.Local)
            throw new InvalidOperationException("Only local users have a password in Relayo.");
        PasswordHash = passwordHash;
    }

    public void Block() => IsBlocked = true;

    public void Unblock() => IsBlocked = false;
}
