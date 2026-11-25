using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Relayo.Core.Data;

public static class RelayoDbContextOptions
{
    /// <summary>Общая настройка контекста: PostgreSQL и имена таблиц и колонок в snake_case.</summary>
    public static DbContextOptionsBuilder UseRelayoPostgres(this DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    /// <summary>Отдельные параметры контекста без интеграций хоста: для миграций и dotnet ef.</summary>
    public static DbContextOptions<RelayoDbContext> Standalone(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<RelayoDbContext>();
        builder.UseRelayoPostgres(connectionString);
        return builder.Options;
    }
}

/// <summary>
/// Нужна только инструменту dotnet ef для создания миграций: к БД при этом он не подключается.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RelayoDbContext>
{
    public RelayoDbContext CreateDbContext(string[] args) =>
        new(RelayoDbContextOptions.Standalone("Host=localhost;Database=relayo_design_time"));
}
