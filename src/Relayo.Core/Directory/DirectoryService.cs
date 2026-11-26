using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relayo.Core.Data;
using Relayo.Core.Security;
using Relayo.Core.Users;

namespace Relayo.Core.Directory;

/// <summary>Настройки для админки. Пароля нет, только признак, что он сохранён.</summary>
public sealed record LdapSettingsView(
    bool IsEnabled,
    string Host,
    int Port,
    LdapSecurity Security,
    bool VerifyCertificate,
    DirectoryProvider Provider,
    string BindDn,
    bool HasBindPassword,
    string UsersDn,
    string GuidAttribute,
    string LoginAttribute,
    string FullNameAttribute,
    string? MailAttribute,
    string? PhoneAttribute,
    string? PositionAttribute,
    UserRole DefaultRole,
    int SyncIntervalMinutes,
    DateTimeOffset? LastCheckAt,
    bool? LastCheckOk,
    string? LastCheckMessage,
    DateTimeOffset? LastSyncAt,
    bool? LastSyncOk,
    string? LastSyncMessage);

/// <param name="BindPassword">Пусто — оставить сохранённый пароль.</param>
public sealed record LdapSettingsInput(
    bool IsEnabled,
    string Host,
    int Port,
    LdapSecurity Security,
    bool VerifyCertificate,
    DirectoryProvider Provider,
    string BindDn,
    string? BindPassword,
    string UsersDn,
    string GuidAttribute,
    string LoginAttribute,
    string FullNameAttribute,
    UserRole DefaultRole,
    int SyncIntervalMinutes,
    string? MailAttribute = null,
    string? PhoneAttribute = null,
    string? PositionAttribute = null);

public sealed record DirectorySyncResult(int Created, int Updated, int Blocked, int Restored, IReadOnlyList<string> Conflicts);

/// <summary>
/// Подключение к AD: настройки, проверка связи, синхронизация папки и вход по учётке AD (п. 3.1 ТЗ).
/// </summary>
public sealed partial class DirectoryService(
    RelayoDbContext db,
    LdapDirectory directory,
    SecretProtector protector,
    TimeProvider time,
    ILogger<DirectoryService> logger)
{
    public async Task<LdapSettingsView> GetAsync(CancellationToken ct) => ToView(await LoadAsync(ct));

    /// <summary>Сохраняет настройки и сразу проверяет подключение: статус виден на той же странице.</summary>
    public async Task<LdapSettingsView> SaveAsync(LdapSettingsInput input, CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        var host = Required(input.Host, "Host");
        var usersDn = Required(input.UsersDn, "Users folder (OU)");
        var bindDn = Required(input.BindDn, "Service account");
        if (input.Port is < 1 or > 65535)
            throw new DomainException(ErrorCodes.InvalidInput, "Port must be 1-65535");
        if (input.SyncIntervalMinutes is < 0 or > 7 * 24 * 60)
            throw new DomainException(ErrorCodes.InvalidInput, "Sync interval must be 0-10080 minutes");
        if (!Enum.IsDefined(input.Security) || !Enum.IsDefined(input.Provider) || !Enum.IsDefined(input.DefaultRole))
            throw new DomainException(ErrorCodes.InvalidInput, "Unknown encryption, provider or role");
        if (host.Contains("://") || host.Contains('/'))
            throw new DomainException(ErrorCodes.InvalidInput, "Host is a server name or IP without ldap:// and path");

        settings.Configure(input.IsEnabled, host, input.Port, input.Security, input.VerifyCertificate, input.Provider,
            bindDn, usersDn, Attribute(input.GuidAttribute, "GUID"), Attribute(input.LoginAttribute, "Login"),
            Attribute(input.FullNameAttribute, "Full name"), input.DefaultRole, input.SyncIntervalMinutes, time.GetUtcNow());
        settings.SetProfileAttributes(OptionalAttribute(input.MailAttribute, "Mail"), OptionalAttribute(input.PhoneAttribute, "Phone"),
            OptionalAttribute(input.PositionAttribute, "Position"));
        if (!string.IsNullOrEmpty(input.BindPassword))
            settings.SetBindPassword(protector.Protect(input.BindPassword));
        if (settings.ProtectedBindPassword is null)
            throw new DomainException(ErrorCodes.InvalidInput, "Service account password is required");
        await db.SaveChangesAsync(ct);

        return await CheckAsync(ct);
    }

    public async Task<LdapSettingsView> CheckAsync(CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        if (!settings.IsConfigured)
            throw new DomainException(ErrorCodes.InvalidInput, "Fill in and save the connection settings first");
        try
        {
            var count = await directory.CheckAsync(settings, BindPassword(settings), ct);
            settings.RecordCheck(true, $"Connected. Users in the folder: {count}", time.GetUtcNow());
        }
        catch (DirectoryException ex)
        {
            logger.LogWarning(ex.InnerException, "LDAP check failed: {Error}", ex.Message);
            settings.RecordCheck(false, ex.Message, time.GetUtcNow());
        }
        await db.SaveChangesAsync(ct);
        return ToView(settings);
    }

    /// <summary>
    /// Синхронизация папки: новые пользователи создаются с ролью по умолчанию, пропавшие и отключённые
    /// блокируются (история сохраняется), вернувшиеся разблокируются. Сопоставление по objectGUID.
    /// </summary>
    public async Task<LdapSettingsView> SyncAsync(CancellationToken ct)
    {
        var settings = await LoadAsync(ct);
        if (!settings.IsConfigured)
            throw new DomainException(ErrorCodes.InvalidInput, "Fill in and save the connection settings first");

        // Две синхронизации одновременно (кнопка и расписание, два экземпляра сервиса) не мешают друг другу.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync("directory-sync", ct);

        try
        {
            var result = await ApplyAsync(settings, await directory.FetchUsersAsync(settings, BindPassword(settings), ct), ct);
            var message = $"Created: {result.Created}, updated: {result.Updated}, blocked: {result.Blocked}, "
                          + $"unblocked: {result.Restored}";
            if (result.Conflicts.Count > 0)
                message += $". Skipped, the login is taken by a local user: {string.Join(", ", result.Conflicts)}";
            settings.RecordSync(true, message, time.GetUtcNow());
            logger.LogInformation("Directory sync: {Result}", message);
        }
        catch (DirectoryException ex)
        {
            logger.LogWarning(ex.InnerException, "Directory sync failed: {Error}", ex.Message);
            settings.RecordSync(false, ex.Message, time.GetUtcNow());
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToView(settings);
    }

    /// <summary>
    /// Вход по учётке AD: пароль проверяет AD. Пользователь должен быть импортирован, не заблокирован,
    /// а objectGUID записи в AD — совпадать с сохранённым (учётку не пересоздали с тем же CN).
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken ct)
    {
        var settings = await db.LdapSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings is not { IsEnabled: true, IsConfigured: true } || user.AdObjectGuid is null)
            return false;
        try
        {
            var guid = await directory.AuthenticateAsync(settings, user.Login, password, ct);
            return guid == user.AdObjectGuid;
        }
        catch (DirectoryException ex)
        {
            logger.LogWarning(ex.InnerException, "AD sign-in check failed: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<DirectorySyncResult> ApplyAsync(LdapSettings settings, IReadOnlyList<DirectoryUser> found,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var existing = await db.Users.Where(u => u.Source == UserSource.ActiveDirectory).ToListAsync(ct);
        var byGuid = existing.Where(u => u.AdObjectGuid != null).ToDictionary(u => u.AdObjectGuid!.Value);
        var active = found.Where(u => !u.IsDisabled).ToList();
        var activeGuids = active.Select(u => u.ObjectGuid).ToHashSet();
        int created = 0, updated = 0, blocked = 0, restored = 0;
        var conflicts = new List<string>();
        var revoke = new List<Guid>();

        foreach (var entry in active)
        {
            var normalized = User.NormalizeLogin(entry.Login);
            if (byGuid.TryGetValue(entry.ObjectGuid, out var user))
            {
                if (user.LoginNormalized != normalized
                    && await db.Users.AnyAsync(u => u.LoginNormalized == normalized && u.Id != user.Id, ct))
                {
                    conflicts.Add(entry.Login);
                    continue;
                }
                if (user.UpdateFromDirectory(entry.Login, entry.FullName, entry.Email, entry.Phone, entry.Position))
                    updated++;
                if (user.RestoreFromDirectory())
                    restored++;
                continue;
            }

            if (await db.Users.AnyAsync(u => u.LoginNormalized == normalized, ct))
            {
                conflicts.Add(entry.Login);
                continue;
            }
            var created2 = User.CreateFromDirectory(entry.Login, entry.FullName, settings.DefaultRole, entry.ObjectGuid, now);
            created2.UpdateFromDirectory(entry.Login, entry.FullName, entry.Email, entry.Phone, entry.Position);
            db.Users.Add(created2);
            created++;
        }

        foreach (var user in existing.Where(u => u.AdObjectGuid is { } g && !activeGuids.Contains(g) && !u.IsBlocked))
        {
            user.BlockByDirectory();
            revoke.Add(user.Id);
            blocked++;
        }

        await db.SaveChangesAsync(ct);
        // Заблокированные сразу теряют все сессии.
        if (revoke.Count > 0)
            await db.RefreshTokens.Where(t => revoke.Contains(t.UserId) && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        return new DirectorySyncResult(created, updated, blocked, restored, conflicts);
    }

    private async Task<LdapSettings> LoadAsync(CancellationToken ct)
    {
        var settings = await db.LdapSettings.SingleOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new LdapSettings();
            db.LdapSettings.Add(settings);
        }
        return settings;
    }

    private string? BindPassword(LdapSettings settings) =>
        settings.ProtectedBindPassword is null ? null : protector.Unprotect(settings.ProtectedBindPassword);

    private static string Required(string? value, string name)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is 0 or > LdapSettings.TextMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"{name} is required (up to {LdapSettings.TextMaxLength} characters)");
        return value;
    }

    private static string? OptionalAttribute(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null : Attribute(value, name);

    private static string Attribute(string? value, string name)
    {
        value = value?.Trim() ?? string.Empty;
        if (!AttributePattern().IsMatch(value))
            throw new DomainException(ErrorCodes.InvalidInput, $"{name} attribute must be an LDAP attribute name, e.g. cn");
        return value;
    }

    private static LdapSettingsView ToView(LdapSettings s) => new(
        s.IsEnabled, s.Host, s.Port, s.Security, s.VerifyCertificate, s.Provider, s.BindDn, s.ProtectedBindPassword is not null,
        s.UsersDn, s.GuidAttribute, s.LoginAttribute, s.FullNameAttribute, s.MailAttribute, s.PhoneAttribute,
        s.PositionAttribute, s.DefaultRole, s.SyncIntervalMinutes,
        s.LastCheckAt, s.LastCheckOk, s.LastCheckMessage, s.LastSyncAt, s.LastSyncOk, s.LastSyncMessage);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,99}$")]
    private static partial Regex AttributePattern();
}
