namespace Relayo.Contracts;

/// <summary>Очереди RabbitMQ, которые слушают сервисы.</summary>
public static class Queues
{
    public const string Internal = "relayo.internal";
    public const string Public = "relayo.public";
}

/// <summary>Заголовки gRPC между публичным и внутренним сервисами.</summary>
public static class GrpcHeaders
{
    /// <summary>Общий ключ: внутренний сервис отклоняет вызовы без него.</summary>
    public const string InternalKey = "x-relayo-internal-key";
}
