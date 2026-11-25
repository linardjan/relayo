using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Relayo.Core.Data;
using Relayo.Core.Security;

namespace Relayo.Core.Sites;

public sealed record ClientTokenOptions(TimeSpan ClockSkew, TimeSpan MaxLifetime);

public sealed record ClientTokenClaims(Guid SiteId, string ClientId, string? Locale);

/// <summary>
/// Проверяет токен клиента, который подписал backend сайта (п. 3.3.1 ТЗ).
/// Сайт определяется по ключу подписи (заголовок kid), поэтому product в токене не нужен.
/// </summary>
public sealed class ClientTokenValidator(RelayoDbContext db, SecretProtector protector, ClientTokenOptions options,
    TimeProvider time)
{
    public const string ClientIdClaim = "clientId";
    public const string LocaleClaim = "locale";

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<ClientTokenClaims?> ValidateAsync(string token, CancellationToken ct)
    {
        JsonWebToken jwt;
        try
        {
            jwt = new JsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(jwt.Kid))
            return null;

        var key = await db.SiteKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.KeyId == jwt.Kid && k.RevokedAt == null, ct);
        if (key is null)
            return null;

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(protector.Unprotect(key.ProtectedSecret)))
            {
                KeyId = key.KeyId,
            },
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = options.ClockSkew,
        });
        if (!result.IsValid)
            return null;

        // Токен должен быть короткоживущим: долгий токен, утёкший из браузера, работал бы слишком долго.
        if (jwt.ValidTo - time.GetUtcNow().UtcDateTime > options.MaxLifetime + options.ClockSkew)
            return null;

        var clientId = result.Claims.TryGetValue(ClientIdClaim, out var raw)
            ? Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim()
            : null;
        if (string.IsNullOrEmpty(clientId) || clientId.Length > Clients.Client.ExternalIdMaxLength)
            return null;

        var locale = result.Claims.TryGetValue(LocaleClaim, out var rawLocale)
            ? Convert.ToString(rawLocale, CultureInfo.InvariantCulture)
            : null;

        return new ClientTokenClaims(key.SiteId, clientId, locale);
    }
}
