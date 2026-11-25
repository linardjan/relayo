namespace Relayo.Contracts.Messages;

/// <summary>
/// Публичный сервис сообщает внутреннему, что запустился.
/// Нужен для проверки связи через RabbitMQ.
/// </summary>
public sealed record PublicServiceStarted(string Instance, DateTimeOffset StartedAt);
