namespace Relayo.Core.Users;

public enum UserRole
{
    Operator,
    Supervisor,
    Admin,
}

/// <summary>Статус оператора (п. 3.1 ТЗ): по нему будет работать автоматическое распределение чатов.</summary>
public enum OperatorStatus
{
    Offline,
    Online,
    Break,
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
    public const int EmailMaxLength = 256;
    public const int PositionMaxLength = 200;
    public const int PhoneMaxLength = 50;

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

    /// <summary>Рабочая почта. Видят только сотрудники. У пользователей из AD приходит из AD.</summary>
    public string? Email { get; private set; }

    public string? Position { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Язык интерфейса админки.</summary>
    public string Locale { get; private set; } = "en";

    public OperatorStatus Status { get; private set; } = OperatorStatus.Offline;

    /// <summary>Группа (отдел), в которую по умолчанию попадают чаты сотрудника. Одна из его групп.</summary>
    public Guid? DefaultGroupId { get; private set; }

    public List<Guid> GroupIds { get; private set; } = [];

    /// <summary>
    /// Заблокирован синхронизацией: пропал из папки AD или отключён там. Вернётся в папку — разблокируется сам.
    /// Блокировку администратором синхронизация не снимает.
    /// </summary>
    public bool BlockedByDirectory { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>id сотрудника в Usedesk: по нему перенесённые сообщения находят автора.</summary>
    public int? UsedeskId { get; private set; }

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

    /// <summary>Пользователь из папки AD. Пароля в Relayo у него нет: вход проверяет AD.</summary>
    public static User CreateFromDirectory(string login, string fullName, UserRole role, Guid objectGuid,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        return new User
        {
            Login = login.Trim(),
            LoginNormalized = NormalizeLogin(login),
            FullName = string.IsNullOrWhiteSpace(fullName) ? login.Trim() : fullName.Trim(),
            Role = role,
            Source = UserSource.ActiveDirectory,
            AdObjectGuid = objectGuid,
            CreatedAt = now,
        };
    }

    /// <summary>Данные из AD при синхронизации: CN могли переименовать, ФИО, почту и телефон — поправить.</summary>
    public bool UpdateFromDirectory(string login, string fullName, string? email, string? phone, string? position)
    {
        var before = (Login, FullName, Email, Phone, Position);
        Login = login.Trim();
        LoginNormalized = NormalizeLogin(login);
        if (!string.IsNullOrWhiteSpace(fullName))
            FullName = fullName.Trim();
        Email = Clean(email, EmailMaxLength);
        Phone = Clean(phone, PhoneMaxLength);
        Position = Clean(position, PositionMaxLength);
        return before != (Login, FullName, Email, Phone, Position);
    }

    /// <summary>Контакты и должность сотрудника. У пользователей из AD их задаёт синхронизация.</summary>
    public void SetProfile(string? email, string? phone, string? position, string locale)
    {
        Email = Clean(email, EmailMaxLength);
        Phone = Clean(phone, PhoneMaxLength);
        Position = Clean(position, PositionMaxLength);
        Locale = locale;
    }

    /// <summary>Группы сотрудника. Группа по умолчанию — одна из них, иначе первая.</summary>
    public void SetGroups(IReadOnlyList<Guid> groupIds, Guid? defaultGroupId)
    {
        GroupIds = groupIds.Distinct().ToList();
        if (defaultGroupId is { } id && GroupIds.Contains(id))
            DefaultGroupId = id;
        else
            DefaultGroupId = GroupIds.Count > 0 ? GroupIds[0] : null;
    }

    public void SetStatus(OperatorStatus status) => Status = status;

    private static string? Clean(string? value, int max)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
    }

    public void BlockByDirectory()
    {
        if (IsBlocked)
            return;
        IsBlocked = true;
        BlockedByDirectory = true;
    }

    /// <summary>Пользователь снова есть в папке AD. Снимается только блокировка, поставленная синхронизацией.</summary>
    public bool RestoreFromDirectory()
    {
        if (!BlockedByDirectory)
            return false;
        IsBlocked = false;
        BlockedByDirectory = false;
        return true;
    }

    public static string NormalizeLogin(string login) => login.Trim().ToUpperInvariant();

    public void SetPasswordHash(string passwordHash)
    {
        if (Source != UserSource.Local)
            throw new InvalidOperationException("Only local users have a password in Relayo.");
        PasswordHash = passwordHash;
    }

    /// <summary>Псевдоним задают администратор и сам оператор. Уникальность не требуется (п. 3.1 ТЗ).</summary>
    public void SetAlias(string alias)
    {
        alias = alias.Trim();
        if (alias.Length is 0 or > AliasMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"Alias must be 1-{AliasMaxLength} characters");
        Alias = alias;
    }

    public void ClearAlias() => Alias = null;

    public void SetFullName(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        FullName = fullName.Trim();
    }

    public void SetRole(UserRole role) => Role = role;

    public void Block() => IsBlocked = true;

    public void Unblock()
    {
        IsBlocked = false;
        BlockedByDirectory = false;
    }
}
