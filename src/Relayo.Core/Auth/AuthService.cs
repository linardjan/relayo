using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;
using Relayo.Core.Directory;
using Relayo.Core.Users;

namespace Relayo.Core.Auth;

public sealed record AuthResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    User User);

/// <summary>Вход, обновление и завершение сессии админки.</summary>
public sealed class AuthService(
    RelayoDbContext db,
    TokenService tokens,
    IPasswordHasher<User> passwordHasher,
    DirectoryService directory,
    TimeProvider time)
{
    /// <summary>
    /// Если уже отозванный refresh-токен предъявлен в течение этого окна после ротации,
    /// это скорее всего две вкладки, обновившие сессию одновременно, а не кража.
    /// Тогда отказываем только в этом запросе, не отзывая остальные сессии.
    /// </summary>
    internal static readonly TimeSpan ReuseGracePeriod = TimeSpan.FromSeconds(30);

    // Хеш для несуществующих пользователей: проверка пароля занимает столько же времени,
    // и по времени ответа нельзя понять, существует ли логин.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString()));

    public async Task<AuthResult?> LoginAsync(string login, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
            return null;

        var normalized = User.NormalizeLogin(login);
        var user = await db.Users.SingleOrDefaultAsync(u => u.LoginNormalized == normalized, ct);

        // Пользователь из AD: пароль проверяет сам AD (LDAP bind), в Relayo он не хранится (п. 3.1 ТЗ).
        if (user is { Source: UserSource.ActiveDirectory, IsBlocked: false })
            return await directory.VerifyPasswordAsync(user, password, ct) ? await IssueAsync(user, replacing: null, ct) : null;

        if (user is null || user.IsBlocked || user.Source != UserSource.Local || user.PasswordHash is null)
        {
            passwordHasher.VerifyHashedPassword(null!, DummyHash.Value, password);
            return null;
        }

        var check = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (check == PasswordVerificationResult.Failed)
            return null;
        if (check == PasswordVerificationResult.SuccessRehashNeeded)
            user.SetPasswordHash(passwordHasher.HashPassword(user, password));

        return await IssueAsync(user, replacing: null, ct);
    }

    public async Task<AuthResult?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
            return null;

        var hash = TokenService.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
            return null;

        var now = time.GetUtcNow();
        if (stored.RevokedAt is { } revokedAt)
        {
            if (now - revokedAt > ReuseGracePeriod)
            {
                // Старый токен предъявили снова: возможно, его украли.
                // Отзываем все активные сессии пользователя.
                await db.RefreshTokens
                    .Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            }
            return null;
        }

        if (!stored.IsActive(now) || stored.User.IsBlocked)
            return null;

        return await IssueAsync(stored.User, replacing: stored, ct);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
            return;

        var hash = TokenService.HashRefreshToken(refreshToken);
        var now = time.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task<AuthResult> IssueAsync(User user, RefreshToken? replacing, CancellationToken ct)
    {
        var (rawRefresh, refreshEntity) = tokens.CreateRefreshToken(user.Id);
        db.RefreshTokens.Add(refreshEntity);
        replacing?.Revoke(time.GetUtcNow(), refreshEntity.Id);
        await db.SaveChangesAsync(ct);

        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user);
        return new AuthResult(accessToken, accessExpiresAt, rawRefresh, refreshEntity.ExpiresAt, user);
    }
}
