using Relayo.Contracts.Grpc;
using Relayo.Public.Configuration;
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

        // Сообщение: JSON {text, clientMessageId, pageUrl} или multipart/form-data (text, clientMessageId, pageUrl, files) — с файлами.
        widget.MapPost("/messages", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, InternalApi api, PublicSettings settings, CancellationToken ct) =>
        {
            var config = await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var request = new SendVisitorMessageRequest { WidgetId = widgetId, VisitorToken = RequiredVisitorToken(http) };
            if (http.Request.HasFormContentType)
            {
                await ReadFormMessageAsync(http.Request, request, config, settings, ct);
            }
            else
            {
                var body = await http.Request.ReadFromJsonAsync<SendMessageRequest>(ct);
                request.Text = body?.Text ?? string.Empty;
                request.ClientMessageId = body?.ClientMessageId ?? string.Empty;
                request.PageUrl = PageUrl(body?.PageUrl);
            }
            var message = await api.SendAsync(request, ct);
            return Results.Ok(WidgetMapping.ToDto(message));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
            ((IEndpointRouteBuilder)widget).ServiceProvider.GetRequiredService<PublicSettings>().AttachmentsMaxBytes
            + FormOverheadBytes));

        // Ссылка для перехода в Telegram. Виджет запрашивает её заранее, а не по клику (п. 3.5 ТЗ).
        widget.MapPost("/telegram-link", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var link = await api.CreateTelegramLinkAsync(widgetId, RequiredVisitorToken(http), ct);
            return Results.Ok(new MessengerLinkDto(link.Url, DateTimeOffset.FromUnixTimeMilliseconds(link.ExpiresAtUnixMs)));
        });

        // Ссылка для перехода в WhatsApp с кодом: по нему первое сообщение попадёт в переписку посетителя.
        widget.MapPost("/whatsapp-link", async (string widgetId, HttpContext http, WidgetConfigCache configs,
            OriginPolicy origins, InternalApi api, CancellationToken ct) =>
        {
            await GetCheckedConfigAsync(widgetId, http, configs, origins, ct);
            var link = await api.CreateWhatsAppLinkAsync(widgetId, RequiredVisitorToken(http), ct);
            return Results.Ok(new MessengerLinkDto(link.Url, DateTimeOffset.FromUnixTimeMilliseconds(link.ExpiresAtUnixMs)));
        });

        return app;
    }

    /// <summary>Запас на поля формы и заголовки частей сверх лимита на файлы.</summary>
    private const long FormOverheadBytes = 1024 * 1024;

    private const int MaxFiles = 10;

    /// <summary>Адрес страницы необязателен: слишком длинный не передаём дальше, остальное проверит внутренний сервис.</summary>
    private static string PageUrl(string? url) => url is { Length: > 0 and <= 4096 } ? url : string.Empty;

    /// <summary>
    /// Быстрая проверка до передачи во внутренний сервис: файлы разрешены, их не больше 10, общий размер
    /// и заявленный тип в пределах ATTACHMENTS_*. Содержимое файлов сверяет с типом внутренний сервис.
    /// </summary>
    private static async Task ReadFormMessageAsync(HttpRequest http, SendVisitorMessageRequest request, WidgetConfig config,
        PublicSettings settings, CancellationToken ct)
    {
        if (http.ContentLength > settings.AttachmentsMaxBytes + FormOverheadBytes)
            throw TooLarge(settings);
        var form = await http.ReadFormAsync(ct);
        request.Text = form["text"].ToString();
        request.ClientMessageId = form["clientMessageId"].ToString();
        request.PageUrl = PageUrl(form["pageUrl"].ToString());
        if (form.Files.Count == 0)
            return;
        if (!config.AllowAttachments)
            throw new WidgetApiException(StatusCodes.Status409Conflict, "attachments_disabled",
                "Attachments are turned off for this widget");
        if (form.Files.Count > MaxFiles)
            throw new WidgetApiException(StatusCodes.Status400BadRequest, "too_many_attachments",
                $"Attach up to {MaxFiles} files to one message");
        if (form.Files.Sum(f => f.Length) > settings.AttachmentsMaxBytes)
            throw TooLarge(settings);

        foreach (var file in form.Files)
        {
            // Тип по расширению (если браузер его не назвал) определит внутренний сервис.
            var type = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
            if (type is not ("" or "application/octet-stream") && settings.AttachmentsAllowedTypes.Length > 0
                && !settings.AttachmentsAllowedTypes.Contains(type))
                throw new WidgetApiException(StatusCodes.Status400BadRequest, "attachment_type_not_allowed",
                    $"File type of '{file.FileName}' is not allowed");
            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            request.Files.Add(new UploadedFile
            {
                FileName = file.FileName,
                ContentType = type,
                Content = Google.Protobuf.ByteString.CopyFrom(buffer.GetBuffer(), 0, (int)buffer.Length),
            });
        }
    }

    private static WidgetApiException TooLarge(PublicSettings settings) =>
        new(StatusCodes.Status413PayloadTooLarge, "attachment_too_large",
            $"Files of one message must be up to {settings.AttachmentsMaxBytes / (1024 * 1024)} MB in total");

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
