using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Relayo.Core.Auth;
using Relayo.Core.Users;

namespace Relayo.Tests;

public class TokenServiceTests
{
    private static readonly JwtOptions Options = new("relayo", "relayo-admin",
        RandomNumberGenerator.GetBytes(32), TimeSpan.FromMinutes(15), TimeSpan.FromDays(14));

    private static User Supervisor() =>
        User.CreateLocal("Petrova", "Maria Petrova", UserRole.Supervisor, "Maria", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Access_token_is_valid_and_carries_role()
    {
        var service = new TokenService(Options, TimeProvider.System);
        var user = Supervisor();

        var (token, expiresAt) = service.CreateAccessToken(user);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, service.ValidationParameters);

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(user.Id.ToString(), result.Claims[TokenService.SubjectClaim]);
        Assert.Equal("Supervisor", result.Claims[TokenService.RoleClaim]);
        Assert.True(expiresAt > DateTimeOffset.UtcNow.AddMinutes(14));
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var other = new TokenService(Options with { SigningKey = RandomNumberGenerator.GetBytes(32) }, TimeProvider.System);
        var (token, _) = other.CreateAccessToken(Supervisor());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            token, new TokenService(Options, TimeProvider.System).ValidationParameters);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Refresh_token_is_stored_only_as_hash()
    {
        var service = new TokenService(Options, TimeProvider.System);
        var userId = Guid.NewGuid();

        var (raw, entity) = service.CreateRefreshToken(userId);
        var (raw2, _) = service.CreateRefreshToken(userId);

        Assert.NotEqual(raw, entity.TokenHash);
        Assert.Equal(TokenService.HashRefreshToken(raw), entity.TokenHash);
        Assert.NotEqual(raw, raw2);
        Assert.True(entity.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Login_is_case_insensitive()
    {
        Assert.Equal(User.NormalizeLogin(" ivanov "), User.NormalizeLogin("IVANOV"));
    }
}
