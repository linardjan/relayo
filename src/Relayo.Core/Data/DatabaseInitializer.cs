using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relayo.Core.Security;
using Relayo.Core.Users;

namespace Relayo.Core.Data;

public sealed record DatabaseInitializerOptions(
    string ConnectionString,
    bool MigrateOnStart,
    string? BootstrapAdminLogin,
    string? BootstrapAdminPassword,
    TestSiteSeed? TestSite);

/// <summary>
/// При запуске применяет миграции, создаёт секции сообщений, первого администратора
/// (если пользователей ещё нет) и в Development — тестовый сайт.
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
        var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();

        var partitions = await MessagePartitions.EnsureAsync(db, time.GetUtcNow(), ct);
        if (partitions.Count > 0)
            logger.LogInformation("Created message partitions: {Partitions}", partitions);

        // Несколько экземпляров запускаются одновременно: первого администратора и тестовые данные
        // создаёт один из них, остальные ждут и видят, что всё уже есть.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.LockAsync("startup-seed", ct);
            await SeedBootstrapAdminAsync(scope.ServiceProvider, ct);
            if (options.TestSite is { } seed)
                await DevSeed.ApplyAsync(db, scope.ServiceProvider.GetRequiredService<SecretProtector>(), seed,
                    time.GetUtcNow(), ct);
            await tx.CommitAsync(ct);
        }

        if (options.TestSite is { } testSite)
            logger.LogInformation("Test site '{Product}' is ready: widgets {TokenWidget} (signed token), {DirectWidget} (direct)",
                testSite.Product, testSite.TokenModeWidgetId, testSite.DirectModeWidgetId);
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

/// <summary>Подготовка сервера БД до запуска хоста.</summary>
public static class DatabaseBootstrap
{
    /// <summary>
    /// Создаёт пустую базу, если её ещё нет. Вызывается до запуска хоста: Wolverine при старте
    /// создаёт свои таблицы и без базы упал бы раньше миграций. Учётке нужно право CREATE DATABASE;
    /// если базу заранее создал администратор СУБД, право не нужно.
    /// </summary>
    public static async Task EnsureDatabaseExistsAsync(string connectionString, ILogger logger, CancellationToken ct)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var name = target.InitialCatalog;
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };

        await using var connection = new SqlConnection(master.ConnectionString);
        // SQL Server в контейнере может ещё запускаться: ждём до минуты.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(ct);
                break;
            }
            catch (SqlException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        await using var exists = new SqlCommand("SELECT DB_ID(@name)", connection);
        exists.Parameters.AddWithValue("@name", name);
        if (await exists.ExecuteScalarAsync(ct) is not DBNull and not null)
            return;

        // Имя базы — из настроек, а не от пользователя; QUOTENAME защищает от ошибок в имени.
        await using var create = new SqlCommand("DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC (@sql);", connection);
        create.Parameters.AddWithValue("@name", name);
        try
        {
            await create.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Created database {Database}", name);
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            // Базу только что создал другой экземпляр, запущенный одновременно.
        }
    }
}
