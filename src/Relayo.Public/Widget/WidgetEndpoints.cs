using Relayo.Contracts.Grpc;
using Relayo.Public.Internal;

namespace Relayo.Public.Widget;

/// <summary>
/// API виджета. Каждый запрос проверяется по Origin: виджет работает только на доменах своего сайта.
/// Посетитель передаёт свой токен в заголовке X-Relayo-Visitor.
/// </summary>
internal static class WidgetEndpoints
{
    public const string VisitorHeader = "X-Relayo-Visitor";

    public static IEndpointRouteBuilder MapWidgetEndpoints(this IEndpointRouteBuilder app)
    {
        var widget = app.MapGroup("/api/widget/{widgetId}")
            .AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (WidgetApiException ex)
                {
                    return Problem(ex.StatusCode, ex.Code, ex.Message);
                }
            });

        // Настройки для отрисовки кнопки виджета. Посетитель при этом не создаётся.
        widget.MapGet("/config", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, CancellationToken ct) =>
        {
            var config = await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            return Results.Ok(WidgetConfigDto.From(config));
        });

        // Посетитель открыл виджет: находим его по токену или создаём, отдаём историю.
        widget.MapPost("/session", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var session = await api.StartVisitorAsync(widgetId, VisitorToken(http), createIfMissing: true, ct);
            var history = await api.GetHistoryAsync(widgetId, session.VisitorToken, ct);
            return Results.Ok(new SessionDto(session.VisitorToken, session.Verified,
                history.Messages.Select(WidgetMapping.ToDto).ToList()));
        });

        widget.MapPost("/identify", async (string widgetId, IdentifyTokenRequest request, HttpContext http,
            WidgetConfigCache configs, OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var session = await api.IdentifyAsync(new IdentifyRequest
            {
                WidgetId = widgetId,
                VisitorToken = RequiredVisitorToken(http),
                SignedToken = request.Token ?? string.Empty,
            }, ct);
            return await SessionWithHistoryAsync(widgetId, session, api, ct);
        });

        widget.MapPost("/identify-direct", async (string widgetId, IdentifyDirectRequest request, HttpContext http,
            WidgetConfigCache configs, OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var session = await api.IdentifyAsync(new IdentifyRequest
            {
                WidgetId = widgetId,
                VisitorToken = RequiredVisitorToken(http),
                Direct = new DirectIdentity
                {
                    ClientId = request.ClientId ?? string.Empty,
                    Product = request.Product ?? string.Empty,
                    Locale = request.Locale ?? string.Empty,
                },
            }, ct);
            return await SessionWithHistoryAsync(widgetId, session, api, ct);
        });

        widget.MapGet("/messages", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var history = await api.GetHistoryAsync(widgetId, RequiredVisitorToken(http), ct);
            return Results.Ok(history.Messages.Select(WidgetMapping.ToDto));
        });

        widget.MapPost("/messages", async (string widgetId, SendMessageRequest request, HttpContext http,
            WidgetConfigCache configs, OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var message = await api.SendAsync(new SendVisitorMessageRequest
            {
                WidgetId = widgetId,
                VisitorToken = RequiredVisitorToken(http),
                Text = request.Text ?? string.Empty,
                ClientMessageId = request.ClientMessageId ?? string.Empty,
            }, ct);
            return Results.Ok(WidgetMapping.ToDto(message));
        });

        // Ссылка для перехода в Telegram. Виджет запрашивает её заранее, а не по клику (п. 3.5 ТЗ).
        widget.MapPost("/telegram-link", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var link = await api.CreateTelegramLinkAsync(widgetId, RequiredVisitorToken(http), ct);
            return Results.Ok(new MessengerLinkDto(link.Url, DateTimeOffset.FromUnixTimeMilliseconds(link.ExpiresAtUnixMs)));
        });

        return app;
    }

    public static async Task<WidgetConfig> GetCheckedConfigAsync(string widgetId, HttpContext http,
        WidgetConfigCache configs, OriginPolicy origins, CancellationToken ct)
    {
        if (!Guid.TryParse(widgetId, out _))
            throw new WidgetApiException(StatusCodes.Status404NotFound, "widget_not_found", "Widget not found");
        var config = await configs.GetAsync(widgetId, ct);
        if (!origins.IsAllowedForWidget(http.Request.Headers.Origin, config))
            throw new WidgetApiException(StatusCodes.Status403Forbidden, "origin_not_allowed",
                "This widget is not allowed on this site");
        return config;
    }

    private static async Task<IResult> SessionWithHistoryAsync(string widgetId, VisitorSession session,
        InternalApi api, CancellationToken ct)
    {
        var history = await api.GetHistoryAsync(widgetId, session.VisitorToken, ct);
        return Results.Ok(new SessionDto(session.VisitorToken, session.Verified,
            history.Messages.Select(WidgetMapping.ToDto).ToList()));
    }

    private static string? VisitorToken(HttpContext http) =>
        http.Request.Headers[VisitorHeader].FirstOrDefault() is { Length: > 0 } token ? token : null;

    private static string RequiredVisitorToken(HttpContext http) =>
        VisitorToken(http) ?? throw new WidgetApiException(StatusCodes.Status401Unauthorized, "visitor_required",
            "Visitor token is missing");

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
