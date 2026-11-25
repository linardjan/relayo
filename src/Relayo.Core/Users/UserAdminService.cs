using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;

namespace Relayo.Core.Users;

public sealed record UserView(
    Guid Id,
    string Login,
    string FullName,
    string? Alias,
    UserRole Role,
    UserSource Source,
    bool IsBlocked,
    bool BlockedByDirectory,
    DateTimeOffset CreatedAt,
    string? Email,
    string? Position,
    string? Phone,
    string Locale,
    OperatorStatus Status,
    IReadOnlyList<Guid> GroupIds,
    Guid? DefaultGroupId);

/// <summary>Контакты, язык и группы — общие поля создания и изменения сотрудника.</summary>
public sealed record UserProfileInput(
    string? Email = null,
    string? Position = null,
    string? Phone = null,
    string? Locale = null,
    IReadOnlyList<Guid>? GroupIds = null,
    Guid? DefaultGroupId = null);

public sealed record NewUserInput(string Login, string FullName, UserRole Role, string? Alias, string Password,
    UserProfileInput? Profile = null);

/// <param name="Password">Новый пароль локальной учётки; пусто — не менять.</param>
public sealed record UserUpdateInput(string FullName, string? Alias, UserRole Role, bool IsBlocked,
    UserProfileInput? Profile = null, string? Password = null);

public sealed record AgentGroupView(Guid Id, string Name, int MemberCount);

/// <summary>Управление сотрудниками (раздел «Настройки», только администратор).</summary>
public sealed class UserAdminService(RelayoDbContext db, IPasswordHasher<User> hasher, TimeProvider time)
{
    public const int MinPasswordLength = 10;

    public async Task<IReadOnlyList<UserView>> ListAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().OrderBy(u => u.IsBlocked).ThenBy(u => u.FullName).ToListAsync(ct))
        .Select(ToView).ToList();

    public async Task<UserView> CreateLocalAsync(NewUserInput input, CancellationToken ct)
    {
        var login = input.Login?.Trim() ?? string.Empty;
        if (login.Length is 0 or > User.LoginMaxLength || login.Any(char.IsWhiteSpace))
            throw new DomainException(ErrorCodes.InvalidInput, "Login must be 1-256 characters without spaces");
        var fullName = ValidFullName(input.FullName);
        ValidatePassword(input.Password);
        if (await db.Users.AnyAsync(u => u.LoginNormalized == User.NormalizeLogin(login), ct))
            throw new DomainException(ErrorCodes.LoginTaken, "This login is already used");

        var user = User.CreateLocal(login, fullName, ValidRole(input.Role), alias: null, time.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(input.Alias))
            user.SetAlias(input.Alias);
        await ApplyProfileAsync(user, input.Profile, ct);
        user.SetPasswordHash(hasher.HashPassword(user, input.Password));
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return ToView(user);
    }

    public async Task<UserView> UpdateAsync(Guid userId, Guid actingUserId, UserUpdateInput input, CancellationToken ct)
    {
        var user = await FindAsync(userId, ct);
        var role = ValidRole(input.Role);

        // Нельзя остаться без администраторов: ни понижением роли, ни блокировкой.
        var losesAdmin = user.Role == UserRole.Admin && !user.IsBlocked && (role != UserRole.Admin || input.IsBlocked);
        if (losesAdmin)
        {
            if (userId == actingUserId)
                throw new DomainException(ErrorCodes.LastAdmin, "You cannot remove your own administrator access");
            var otherAdmins = await db.Users.CountAsync(u => u.Role == UserRole.Admin && !u.IsBlocked && u.Id != userId, ct);
            if (otherAdmins == 0)
                throw new DomainException(ErrorCodes.LastAdmin, "At least one active administrator is required");
        }

        // ФИО, почта, телефон и должность пользователя из AD приходят из AD: синхронизация их перезапишет.
        if (user.Source == UserSource.Local)
            user.SetFullName(ValidFullName(input.FullName));
        await ApplyProfileAsync(user, input.Profile, ct);
        var passwordChanged = false;
        if (!string.IsNullOrEmpty(input.Password))
        {
            if (user.Source != UserSource.Local)
                throw new DomainException(ErrorCodes.InvalidInput, "Password of an Active Directory user is managed in AD");
            ValidatePassword(input.Password);
            user.SetPasswordHash(hasher.HashPassword(user, input.Password));
            passwordChanged = true;
        }
        if (string.IsNullOrWhiteSpace(input.Alias))
            user.ClearAlias();
        else
            user.SetAlias(input.Alias);
        user.SetRole(role);
        if (input.IsBlocked)
            user.Block();
        else if (user.IsBlocked)
            user.Unblock();

        await db.SaveChangesAsync(ct);

        // Заблокированный пользователь и пользователь с новым паролем сразу теряют все сессии.
        if (input.IsBlocked || passwordChanged)
            await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, time.GetUtcNow()), ct);

        return ToView(user);
    }

    public async Task SetPasswordAsync(Guid userId, string password, CancellationToken ct)
    {
        var user = await FindAsync(userId, ct);
        if (user.Source != UserSource.Local)
            throw new DomainException(ErrorCodes.InvalidInput, "Password of an Active Directory user is managed in AD");
        ValidatePassword(password);
        user.SetPasswordHash(hasher.HashPassword(user, password));
        await db.SaveChangesAsync(ct);

        // После смены пароля старые сессии больше не действуют.
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, time.GetUtcNow()), ct);
    }

    public async Task<UserView> SetStatusAsync(Guid userId, OperatorStatus status, CancellationToken ct)
    {
        if (!Enum.IsDefined(status))
            throw new DomainException(ErrorCodes.InvalidInput, "Unknown status");
        var user = await FindAsync(userId, ct);
        user.SetStatus(status);
        await db.SaveChangesAsync(ct);
        return ToView(user);
    }

    /// <summary>
    /// Удалить можно только учётку без переписки и тикетов: иначе пропали бы авторы сообщений в истории.
    /// Такую учётку блокируют. Пользователей из AD удаляют из папки AD — синхронизация их заблокирует.
    /// </summary>
    public async Task DeleteAsync(Guid userId, Guid actingUserId, CancellationToken ct)
    {
        var user = await FindAsync(userId, ct);
        if (userId == actingUserId)
            throw new DomainException(ErrorCodes.LastAdmin, "You cannot delete yourself");
        if (user.Source == UserSource.ActiveDirectory)
            throw new DomainException(ErrorCodes.HasHistory, "Active Directory users are removed from the AD folder; sync then blocks them");
        if (user.Role == UserRole.Admin && !user.IsBlocked
            && !await db.Users.AnyAsync(u => u.Role == UserRole.Admin && !u.IsBlocked && u.Id != userId, ct))
            throw new DomainException(ErrorCodes.LastAdmin, "At least one active administrator is required");
        if (await db.Tickets.AnyAsync(t => t.AssigneeId == userId, ct)
            || await db.Messages.AnyAsync(m => m.OperatorId == userId, ct))
            throw new DomainException(ErrorCodes.HasHistory, "The agent has conversation history. Block the agent instead of deleting");

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
    }

    // ---- Группы агентов ----

    public async Task<IReadOnlyList<AgentGroupView>> ListGroupsAsync(CancellationToken ct)
    {
        var groups = await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var memberships = await db.Users.AsNoTracking().Select(u => u.GroupIds).ToListAsync(ct);
        return groups.Select(g => new AgentGroupView(g.Id, g.Name, memberships.Count(ids => ids.Contains(g.Id)))).ToList();
    }

    public async Task<AgentGroupView> CreateGroupAsync(string name, CancellationToken ct)
    {
        var group = new AgentGroup(name ?? string.Empty, time.GetUtcNow());
        if (await db.AgentGroups.AnyAsync(g => g.Name == group.Name, ct))
            throw new DomainException(ErrorCodes.GroupNameTaken, "A group with this name already exists");
        db.AgentGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return new AgentGroupView(group.Id, group.Name, 0);
    }

    public async Task<AgentGroupView> RenameGroupAsync(Guid groupId, string name, CancellationToken ct)
    {
        var group = await db.AgentGroups.SingleOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw new DomainException(ErrorCodes.GroupNotFound, "Group not found");
        group.Rename(name ?? string.Empty);
        if (await db.AgentGroups.AnyAsync(g => g.Name == group.Name && g.Id != groupId, ct))
            throw new DomainException(ErrorCodes.GroupNameTaken, "A group with this name already exists");
        await db.SaveChangesAsync(ct);
        return (await ListGroupsAsync(ct)).Single(g => g.Id == groupId);
    }

    /// <summary>Удаление группы: сотрудники из неё выходят, группа по умолчанию у них меняется на другую.</summary>
    public async Task DeleteGroupAsync(Guid groupId, CancellationToken ct)
    {
        var group = await db.AgentGroups.SingleOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw new DomainException(ErrorCodes.GroupNotFound, "Group not found");
        foreach (var user in await db.Users.Where(u => u.GroupIds.Contains(groupId)).ToListAsync(ct))
            user.SetGroups(user.GroupIds.Where(id => id != groupId).ToList(), user.DefaultGroupId);
        db.AgentGroups.Remove(group);
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyProfileAsync(User user, UserProfileInput? profile, CancellationToken ct)
    {
        if (profile is null)
            return;
        var locale = string.IsNullOrWhiteSpace(profile.Locale) ? user.Locale : profile.Locale.Trim().ToLowerInvariant();
        if (locale is not ("en" or "ru"))
            throw new DomainException(ErrorCodes.InvalidInput, "Language must be en or ru");
        if (user.Source == UserSource.Local)
        {
            var email = profile.Email?.Trim();
            if (!string.IsNullOrEmpty(email) && (email.Length > User.EmailMaxLength || !MailAddress.TryCreate(email, out _)))
                throw new DomainException(ErrorCodes.InvalidInput, "Email address is not valid");
            if (profile.Phone is { Length: > User.PhoneMaxLength } || profile.Position is { Length: > User.PositionMaxLength })
                throw new DomainException(ErrorCodes.InvalidInput, "Phone or position is too long");
            user.SetProfile(email, profile.Phone, profile.Position, locale);
        }
        else
        {
            user.SetProfile(user.Email, user.Phone, user.Position, locale);
        }

        var groupIds = (profile.GroupIds ?? []).Distinct().ToList();
        if (groupIds.Count > 0 && await db.AgentGroups.CountAsync(g => groupIds.Contains(g.Id), ct) != groupIds.Count)
            throw new DomainException(ErrorCodes.GroupNotFound, "Group not found");
        user.SetGroups(groupIds, profile.DefaultGroupId);
    }

    private async Task<User> FindAsync(Guid userId, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct)
        ?? throw new DomainException(ErrorCodes.UserNotFound, "User not found");

    private static string ValidFullName(string? fullName)
    {
        fullName = fullName?.Trim() ?? string.Empty;
        if (fullName.Length is 0 or > User.FullNameMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, "Full name must be 1-256 characters");
        return fullName;
    }

    private static UserRole ValidRole(UserRole role) =>
        Enum.IsDefined(role) ? role : throw new DomainException(ErrorCodes.InvalidInput, "Unknown role");

    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new DomainException(ErrorCodes.WeakPassword, $"Password must be at least {MinPasswordLength} characters");
    }

    public static UserView ToView(User u) =>
        new(u.Id, u.Login, u.FullName, u.Alias, u.Role, u.Source, u.IsBlocked, u.BlockedByDirectory, u.CreatedAt,
            u.Email, u.Position, u.Phone, u.Locale, u.Status, u.GroupIds, u.DefaultGroupId);
}
