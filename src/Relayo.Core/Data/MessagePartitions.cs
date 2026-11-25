using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relayo.Core.Data;

/// <summary>
/// Секции таблицы messages по месяцам (UTC), п. 5.1 ТЗ.
/// Создаются заранее, чтобы запись нового сообщения никогда не упиралась в отсутствующую секцию.
/// Перенос старых секций в архив появится вместе с архивом.
/// </summary>
public static class MessagePartitions
{
    /// <summary>Сколько месяцев вперёд держать готовые секции.</summary>
    public const int MonthsAhead = 2;

    public static string PartitionName(DateOnly month) =>
        $"messages_y{month.Year:D4}m{month.Month:D2}";

    public static async Task<IReadOnlyList<string>> EnsureAsync(RelayoDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var current = new DateOnly(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
        var created = new List<string>();
        for (var offset = -1; offset <= MonthsAhead; offset++)
        {
            var from = current.AddMonths(offset);
            var to = from.AddMonths(1);
            var name = PartitionName(from);
            var exists = await db.Database
                .SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_class WHERE relname = {name}")
                .AnyAsync(ct);
            if (exists)
                continue;

            // Имя секции и даты формируются кодом, а не пользователем, поэтому подстановка в SQL безопасна.
            var sql = string.Create(CultureInfo.InvariantCulture,
                $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF messages FOR VALUES FROM ('{from:yyyy-MM-dd} 00:00:00+00') TO ('{to:yyyy-MM-dd} 00:00:00+00')");
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
            created.Add(name);
        }
        return created;
    }
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
