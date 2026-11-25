namespace Relayo.Core.Auth;

public sealed record JwtOptions(
    string Issuer,
    string Audience,
    byte[] SigningKey,
    TimeSpan AccessTokenLifetime,
    TimeSpan RefreshTokenLifetime);
