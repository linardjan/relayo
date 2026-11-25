using Relayo.Core.Users;

namespace Relayo.Core.Auth;

/// <summary>
/// Refresh-токен сессии админки. В БД хранится только SHA-256 от токена,
/// сам токен знает только браузер (в httpOnly cookie).
/// При каждом обновлении токен заменяется новым (ротация).
/// </summary>
public sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    public RefreshToken(Guid userId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    public User User { get; private set; } = null!;

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Токен, которым заменили этот при ротации.</summary>
    public Guid? ReplacedById { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, Guid? replacedById = null)
    {
        RevokedAt ??= now;
        ReplacedById ??= replacedById;
    }
}
