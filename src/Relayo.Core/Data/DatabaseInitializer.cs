using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relayo.Core.Users;

namespace Relayo.Core.Data;

public sealed record DatabaseInitializerOptions(
    string ConnectionString,
    bool MigrateOnStart,
    string? BootstrapAdminLogin,
    string? BootstrapAdminPassword);

/// <summary>
/// При запуске применяет миграции и создаёт первого администратора, если пользователей ещё нет.
/// Миграции идут через отдельный контекст без интеграции с Wolverine,
/// чтобы модель совпадала со снимком миграций.
/// </summary>
internal sealed class DatabaseInitializer(
    DatabaseInitializerOptions options,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (options.MigrateOnStart)
        {
            await using var migrationContext = new RelayoDbContext(
                RelayoDbContextOptions.Standalone(options.ConnectionString));
            var pending = (await migrationContext.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, pending);
                await migrationContext.Database.MigrateAsync(ct);
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        await SeedBootstrapAdminAsync(scope.ServiceProvider, ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task SeedBootstrapAdminAsync(IServiceProvider services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapAdminLogin) || string.IsNullOrEmpty(options.BootstrapAdminPassword))
            return;

        var db = services.GetRequiredService<RelayoDbContext>();
        if (await db.Users.AnyAsync(ct))
            return;

        var admin = User.CreateLocal(options.BootstrapAdminLogin, "Administrator", UserRole.Admin,
            alias: null, time.GetUtcNow());
        admin.SetPasswordHash(services.GetRequiredService<IPasswordHasher<User>>()
            .HashPassword(admin, options.BootstrapAdminPassword));
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        logger.LogWarning("Created bootstrap administrator '{Login}'. Change the password after first login.",
            admin.Login);
    }
}
