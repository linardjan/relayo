namespace Relayo.Contracts;

/// <summary>Очереди и обменники RabbitMQ.</summary>
public static class Queues
{
    /// <summary>Очередь внутреннего сервиса.</summary>
    public const string Internal = "relayo.internal";

    /// <summary>
    /// Fanout-обменник событий для виджетов. У каждого экземпляра публичного сервиса
    /// своя временная очередь, привязанная к нему: событие получают все экземпляры.
    /// </summary>
    public const string WidgetEvents = "relayo.widget-events";

    public static string PublicInstanceQueue(string instanceId) => $"relayo.public.{instanceId}";
}

/// <summary>Заголовки gRPC между публичным и внутренним сервисами.</summary>
public static class GrpcHeaders
{
    /// <summary>Общий ключ: внутренний сервис отклоняет вызовы без него.</summary>
    public const string InternalKey = "x-relayo-internal-key";
}
