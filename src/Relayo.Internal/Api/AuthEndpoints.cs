using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Auth;
using Relayo.Core.Data;
using Relayo.Core.Users;
using Relayo.Internal.Configuration;

namespace Relayo.Internal.Api;

public sealed record LoginRequest(string Login, string Password);

public sealed record UserDto(Guid Id, string Login, string FullName, string? Alias, string Role, string Source)
{
    public static UserDto From(User user) =>
        new(user.Id, user.Login, user.FullName, user.Alias, user.Role.ToString(), user.Source.ToString());
}

public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserDto User);

/// <summary>
/// Вход в админку. Access-токен отдаётся в теле ответа и хранится в памяти страницы.
/// Refresh-токен лежит в httpOnly cookie: скрипты страницы его не видят.
/// </summary>
internal static class AuthEndpoints
{
    public const string RefreshCookie = "relayo_refresh";
    public const string LoginRateLimitPolicy = "login";
    private const string CookiePath = "/api/auth";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").AllowAnonymous();

        auth.MapPost("/login", async (LoginRequest request, AuthService service, HttpContext http,
                InternalSettings settings, CancellationToken ct) =>
            {
                var result = await service.LoginAsync(request.Login, request.Password, ct);
                if (result is null)
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                        title: "Invalid login or password");
                return Success(result, http, settings);
            })
            .RequireRateLimiting(LoginRateLimitPolicy);

        auth.MapPost("/refresh", async (AuthService service, HttpContext http, InternalSettings settings,
                CancellationToken ct) =>
            {
                var token = http.Request.Cookies[RefreshCookie];
                var result = token is null ? null : await service.RefreshAsync(token, ct);
                if (result is null)
                {
                    DeleteCookie(http, settings);
                    return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Session expired");
                }
                return Success(result, http, settings);
            });

        auth.MapPost("/logout", async (AuthService service, HttpContext http, InternalSettings settings,
                CancellationToken ct) =>
            {
                if (http.Request.Cookies[RefreshCookie] is { } token)
                    await service.LogoutAsync(token, ct);
                DeleteCookie(http, settings);
                return Results.NoContent();
            });

        api.MapGet("/me", async (ClaimsPrincipal principal, RelayoDbContext db, CancellationToken ct) =>
            {
                if (!Guid.TryParse(principal.FindFirstValue(TokenService.SubjectClaim), out var userId))
                    return Results.Unauthorized();
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
                return user is null || user.IsBlocked ? Results.Unauthorized() : Results.Ok(UserDto.From(user));
            })
            .RequireAuthorization();

        return api;
    }

    private static IResult Success(AuthResult result, HttpContext http, InternalSettings settings)
    {
        http.Response.Cookies.Append(RefreshCookie, result.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = settings.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = result.RefreshTokenExpiresAt,
        });
        return Results.Ok(new AuthResponse(result.AccessToken, result.AccessTokenExpiresAt, UserDto.From(result.User)));
    }

    private static void DeleteCookie(HttpContext http, InternalSettings settings) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = settings.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
        });
}
