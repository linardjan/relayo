using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Relayo.Core.Users;

namespace Relayo.Core.Auth;

/// <summary>Выпуск access-токенов (JWT) и refresh-токенов для админки.</summary>
public sealed class TokenService(JwtOptions options, TimeProvider time)
{
    public const string RoleClaim = "role";
    public const string NameClaim = "name";
    public const string SubjectClaim = "sub";

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _key = new(options.SigningKey);

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user)
    {
        var now = time.GetUtcNow();
        var expiresAt = now + options.AccessTokenLifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(SubjectClaim, user.Id.ToString()),
                new Claim(NameClaim, user.FullName),
                new Claim(RoleClaim, user.Role.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expiresAt);
    }

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = _key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = NameClaim,
        RoleClaimType = RoleClaim,
    };

    /// <summary>Создаёт refresh-токен: сам токен отдаётся браузеру, в БД идёт только хеш.</summary>
    public (string RawToken, RefreshToken Entity) CreateRefreshToken(Guid userId)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        return (raw, new RefreshToken(userId, HashRefreshToken(raw), now, now + options.RefreshTokenLifetime));
    }

    public static string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
