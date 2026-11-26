using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relayo.Core.Data;

/// <summary>
/// Секции таблицы messages по месяцам (UTC), п. 5.1 ТЗ.
/// В SQL Server таблица лежит на схеме секционирования <see cref="Scheme"/> по created_at,
/// границы секций — начала месяцев в функции <see cref="Function"/> (RANGE RIGHT).
/// Границы создаются заранее: новая секция отделяется, пока она пустая, и это не двигает данные.
/// Перенос старых секций в архив появится вместе с архивом.
/// </summary>
public static class MessagePartitions
{
    public const string Function = "pf_messages_month";
    public const string Scheme = "ps_messages_month";

    /// <summary>Сколько месяцев вперёд держать готовые секции.</summary>
    public const int MonthsAhead = 2;

    /// <summary>SQL для миграции: функция и схема секционирования с границей на начало месяца <paramref name="month"/>.</summary>
    public static string CreateSql(DateOnly month) => string.Create(CultureInfo.InvariantCulture, $"""
        CREATE PARTITION FUNCTION {Function} (datetimeoffset(7)) AS RANGE RIGHT FOR VALUES ('{Boundary(month)}');
        CREATE PARTITION SCHEME {Scheme} AS PARTITION {Function} ALL TO ([PRIMARY]);
        """);

    public static string PartitionName(DateOnly month) => $"messages {month:yyyy-MM}";

    public static async Task<IReadOnlyList<string>> EnsureAsync(RelayoDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Экземпляры сервиса делают это одновременно: границу добавляет один, остальные её уже видят.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync("message-partitions", ct);
        var created = await EnsureLockedAsync(db, now, ct);
        await tx.CommitAsync(ct);
        return created;
    }

    private static async Task<IReadOnlyList<string>> EnsureLockedAsync(RelayoDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var current = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        var existing = (await db.Database.SqlQuery<DateTimeOffset>($"""
                SELECT CAST(v.value AS datetimeoffset(7)) AS Value
                FROM sys.partition_range_values v
                JOIN sys.partition_functions f ON f.function_id = v.function_id
                WHERE f.name = {Function}
                """).ToListAsync(ct))
            .Select(v => DateOnly.FromDateTime(v.UtcDateTime))
            .ToHashSet();

        var created = new List<string>();
        for (var offset = -1; offset <= MonthsAhead + 1; offset++)
        {
            var month = current.AddMonths(offset);
            if (existing.Contains(month))
                continue;

            // Граница формируется кодом, а не пользователем, поэтому подстановка в SQL безопасна.
            var sql = string.Create(CultureInfo.InvariantCulture, $"""
                ALTER PARTITION SCHEME {Scheme} NEXT USED [PRIMARY];
                ALTER PARTITION FUNCTION {Function}() SPLIT RANGE ('{Boundary(month)}');
                """);
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
            created.Add(PartitionName(month));
        }
        return created;
    }

    private static string Boundary(DateOnly month) => $"{month:yyyy-MM-dd}T00:00:00+00:00";
}

/// <summary>Раз в 6 часов проверяет, что секции на ближайшие месяцы созданы.</summary>
internal sealed class MessagePartitionMaintenance(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<MessagePartitionMaintenance> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();
                var created = await MessagePartitions.EnsureAsync(db, time.GetUtcNow(), ct);
                if (created.Count > 0)
                    logger.LogInformation("Created message partitions: {Partitions}", created);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not create message partitions");
            }
        }
    }
}
