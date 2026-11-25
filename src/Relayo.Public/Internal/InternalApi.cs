using Grpc.Core;
using Relayo.Contracts.Grpc;

namespace Relayo.Public.Internal;

/// <summary>Ошибка для ответа виджету: HTTP-статус и код ошибки от внутреннего сервиса.</summary>
public sealed class WidgetApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static WidgetApiException From(RpcException ex)
    {
        var status = ex.StatusCode switch
        {
            global::Grpc.Core.StatusCode.NotFound => StatusCodes.Status404NotFound,
            global::Grpc.Core.StatusCode.Unauthenticated => StatusCodes.Status401Unauthorized,
            global::Grpc.Core.StatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
            global::Grpc.Core.StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
            global::Grpc.Core.StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        var code = status == StatusCodes.Status503ServiceUnavailable ? "service_unavailable" : ex.Status.Detail;
        var message = status == StatusCodes.Status503ServiceUnavailable ? "Service is temporarily unavailable" : ex.Message;
        return new WidgetApiException(status, code, message);
    }
}

/// <summary>Вызовы внутреннего сервиса с таймаутом и переводом ошибок gRPC в ошибки API.</summary>
public sealed class InternalApi(InternalGateway.InternalGatewayClient client)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);

    public Task<AllowedOrigins> GetAllowedOriginsAsync(CancellationToken ct) =>
        Call(() => client.GetAllowedOriginsAsync(new GetAllowedOriginsRequest(), deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    public Task<WidgetConfig> GetWidgetAsync(string widgetId, CancellationToken ct) =>
        Call(() => client.GetWidgetAsync(new WidgetRequest { WidgetId = widgetId }, deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    public Task<VisitorSession> StartVisitorAsync(string widgetId, string? visitorToken, bool createIfMissing,
        CancellationToken ct) =>
        Call(() => client.StartVisitorAsync(new StartVisitorRequest
        {
            WidgetId = widgetId,
            VisitorToken = visitorToken ?? string.Empty,
            CreateIfMissing = createIfMissing,
        }, deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    public Task<VisitorSession> IdentifyAsync(IdentifyRequest request, CancellationToken ct) =>
        Call(() => client.IdentifyAsync(request, deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    /// <summary>С файлами вызов идёт дольше: внутренний сервис ещё кладёт их в S3.</summary>
    public Task<WidgetMessage> SendAsync(SendVisitorMessageRequest request, CancellationToken ct) =>
        Call(() => client.SendVisitorMessageAsync(request,
            deadline: request.Files.Count == 0 ? Deadline() : DateTime.UtcNow.Add(UploadTimeout),
            cancellationToken: ct).ResponseAsync);

    public Task<WidgetHistory> GetHistoryAsync(string widgetId, string visitorToken, CancellationToken ct) =>
        Call(() => client.GetVisitorHistoryAsync(new VisitorRequest { WidgetId = widgetId, VisitorToken = visitorToken },
            deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    public Task<MessengerLink> CreateTelegramLinkAsync(string widgetId, string visitorToken, CancellationToken ct) =>
        Call(() => client.CreateTelegramLinkAsync(new VisitorRequest { WidgetId = widgetId, VisitorToken = visitorToken },
            deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    public Task<TelegramUpdateReply> HandleTelegramUpdateAsync(TelegramUpdateRequest request, CancellationToken ct) =>
        Call(() => client.HandleTelegramUpdateAsync(request, deadline: Deadline(), cancellationToken: ct).ResponseAsync);

    private static DateTime Deadline() => DateTime.UtcNow.Add(Timeout);

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RpcException ex)
        {
            throw WidgetApiException.From(ex);
        }
    }
}
