using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Relayo.Core.Auth;
using Relayo.Core.Data;
using Relayo.Core.Users;

namespace Relayo.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Сервисы ядра. DbContext регистрирует хост: внутреннему сервису он нужен
    /// с интеграцией Wolverine (транзакционный outbox).
    /// </summary>
    public static IServiceCollection AddRelayoCore(
        this IServiceCollection services, JwtOptions jwt, DatabaseInitializerOptions database)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(jwt);
        services.AddSingleton<TokenService>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<AuthService>();

        services.AddSingleton(database);
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }
}
