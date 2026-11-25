using Relayo.Contracts.Grpc;
using Relayo.Public.Internal;

namespace Relayo.Public.Telegram;

/// <summary>
/// Webhook Telegram (режим TELEGRAM_UPDATES_MODE=webhook). Публичный сервис только пересылает обновление
/// внутреннему по gRPC: секрет webhook и токен бота знает лишь внутренний сервис.
/// </summary>
internal static class TelegramWebhookEndpoints
{
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>Обновление с текстом укладывается в несколько килобайт; больше Telegram не присылает.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapTelegramWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/telegram/{botId:guid}/webhook", async (Guid botId, HttpContext http, InternalApi api,
            ILogger<TelegramWebhookMarker> logger, CancellationToken ct) =>
        {
            var secret = http.Request.Headers[SecretHeader].FirstOrDefault();
            if (string.IsNullOrEmpty(secret))
                return Results.Unauthorized();
            if (http.Request.ContentLength > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            try
            {
                await api.HandleTelegramUpdateAsync(new TelegramUpdateRequest
                {
                    BotId = botId.ToString(),
                    Secret = secret,
                    UpdateJson = body,
                }, ct);
                return Results.Ok();
            }
            catch (WidgetApiException ex) when (ex.StatusCode == StatusCodes.Status401Unauthorized)
            {
                return Results.Unauthorized();
            }
            catch (WidgetApiException ex)
            {
                // Ответ не 2xx: Telegram повторит обновление позже, дубль не появится.
                logger.LogWarning("Telegram update for bot {BotId} was not processed: {Code}", botId, ex.Code);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        return app;
    }

    internal sealed class TelegramWebhookMarker;
}
