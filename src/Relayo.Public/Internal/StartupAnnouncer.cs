using Relayo.Contracts.Messages;
using Wolverine;

namespace Relayo.Public.Internal;

/// <summary>
/// После запуска отправляет внутреннему сервису сообщение через RabbitMQ:
/// так видно, что шина между сервисами работает.
/// </summary>
internal sealed class StartupAnnouncer(
    IHostApplicationLifetime lifetime,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<StartupAnnouncer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        lifetime.ApplicationStarted.Register(() => _ = AnnounceAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task AnnounceAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new PublicServiceStarted(
                $"{Environment.MachineName}:{Environment.ProcessId}", time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not announce startup to the internal service");
        }
    }
}
