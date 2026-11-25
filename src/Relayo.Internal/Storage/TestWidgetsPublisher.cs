using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;
using Relayo.Core.Sites;
using Relayo.Internal.Configuration;

namespace Relayo.Internal.Storage;

/// <summary>
/// Только Development: публикует тестовые виджеты, если их ещё не публиковали или настройки менялись,
/// чтобы TestSite сразу работал с файлом widget_&lt;ключ&gt;.js из S3.
/// </summary>
internal sealed class TestWidgetsPublisher(
    InternalSettings settings,
    IFileStorage storage,
    IServiceScopeFactory scopes,
    ILogger<TestWidgetsPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (settings.Database.TestSite is not { } seed || !storage.IsConfigured)
            return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<WidgetPublisher>();
            var ids = new[] { seed.TokenModeWidgetId, seed.DirectModeWidgetId };
            foreach (var widget in await db.Widgets.AsNoTracking().Where(w => ids.Contains(w.Id)).ToListAsync(ct))
            {
                if (!widget.HasUnpublishedChanges)
                    continue;
                await publisher.PublishAsync(widget.Id, userId: null, ct);
                logger.LogInformation("Published test widget {Key}: {Url}", widget.PublicKey, publisher.FileUrl(widget.PublicKey));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not publish test widgets (is S3 running?): {Error}", ex.Message);
        }
    }
}
