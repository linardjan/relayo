using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relayo.Core.Data;

namespace Relayo.Core.Directory;

/// <summary>Синхронизация папки AD по расписанию из настроек (раз в SyncIntervalMinutes, если AD включён).</summary>
internal sealed class DirectorySyncScheduler(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<DirectorySyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();
                var settings = await db.LdapSettings.AsNoTracking().SingleOrDefaultAsync(stop);
                if (settings is { IsEnabled: true, IsConfigured: true, SyncIntervalMinutes: > 0 }
                    && (settings.LastSyncAt is null
                        || time.GetUtcNow() - settings.LastSyncAt >= TimeSpan.FromMinutes(settings.SyncIntervalMinutes)))
                {
                    await scope.ServiceProvider.GetRequiredService<DirectoryService>().SyncAsync(stop);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled directory sync failed");
            }

            try
            {
                await Task.Delay(CheckEvery, time, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
