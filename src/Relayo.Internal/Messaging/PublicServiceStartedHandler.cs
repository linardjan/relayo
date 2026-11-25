using Relayo.Contracts.Messages;

namespace Relayo.Internal.Messaging;

/// <summary>Проверка связи через RabbitMQ: пишет в лог, что публичный сервис запустился.</summary>
public sealed class PublicServiceStartedHandler(ILogger<PublicServiceStartedHandler> logger)
{
    public void Handle(PublicServiceStarted message) =>
        logger.LogInformation("Public service instance {Instance} started at {StartedAt}",
            message.Instance, message.StartedAt);
}
