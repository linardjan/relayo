using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Relayo.Internal.Channels.Telegram;

/// <summary>Ошибка Bot API. Текст ошибки приходит от Telegram, токена бота в нём нет.</summary>
public sealed class TelegramApiException(int errorCode, string description, int? retryAfterSeconds)
    : Exception($"Telegram API error {errorCode}: {description}")
{
    public int ErrorCode { get; } = errorCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    /// <summary>Повтор не поможет: неверный токен, бот заблокирован пользователем, чат не найден.</summary>
    public bool IsPermanent => ErrorCode is 400 or 401 or 403 or 404;
}

/// <summary>
/// Вызовы Telegram Bot API. Токен бота входит в адрес запроса (https://api.telegram.org/bot&lt;token&gt;/method),
/// поэтому логирование HTTP для этого клиента выключено, а в трассировке адрес маскируется.
/// </summary>
/// <remarks>
/// Синглтон поверх IHttpClientFactory, а не типизированный HttpClient: Wolverine внедряет зависимости
/// в обработчики без service location, а типизированный клиент регистрируется фабрикой.
/// </remarks>
public sealed partial class TelegramBotApi(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "telegram";

    /// <summary>Сколько секунд Telegram держит long polling getUpdates.</summary>
    public const int PollTimeoutSeconds = 25;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] AllowedUpdates = ["message"];

    public static bool IsValidToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    public Task<TgUser> GetMeAsync(string token, CancellationToken ct) =>
        CallAsync<TgUser>(token, "getMe", null, ct);

    public Task<bool> SetWebhookAsync(string token, string url, string secret, CancellationToken ct) =>
        CallAsync<bool>(token, "setWebhook", new { url, secret_token = secret, allowed_updates = AllowedUpdates }, ct);

    public Task<bool> DeleteWebhookAsync(string token, CancellationToken ct) =>
        CallAsync<bool>(token, "deleteWebhook", new { drop_pending_updates = false }, ct);

    public Task<TgUpdate[]> GetUpdatesAsync(string token, long offset, CancellationToken ct) =>
        CallAsync<TgUpdate[]>(token, "getUpdates",
            new { offset, timeout = PollTimeoutSeconds, allowed_updates = AllowedUpdates }, ct);

    public Task<TgMessage> SendMessageAsync(string token, long chatId, string html, CancellationToken ct) =>
        CallAsync<TgMessage>(token, "sendMessage",
            new { chat_id = chatId, text = html, parse_mode = "HTML", link_preview_options = new { is_disabled = true } }, ct);

    /// <summary>
    /// Файл: картинка — sendPhoto (Telegram покажет её в чате), остальное — sendDocument.
    /// Подпись в HTML, до 1024 символов.
    /// </summary>
    public Task<TgMessage> SendFileAsync(string token, long chatId, byte[] content, string fileName, string contentType,
        string? captionHtml, CancellationToken ct)
    {
        // sendPhoto принимает до 10 МБ и пережимает картинку; GIF лучше отправить файлом, иначе он станет статичным.
        var asPhoto = contentType is "image/jpeg" or "image/png" or "image/webp" && content.Length <= 10 * 1024 * 1024;
        var field = asPhoto ? "photo" : "document";
        var form = new MultipartFormDataContent
        {
            { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" },
        };
        if (!string.IsNullOrEmpty(captionHtml))
        {
            form.Add(new StringContent(captionHtml), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
        }
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, field, fileName);
        return PostAsync<TgMessage>(token, asPhoto ? "sendPhoto" : "sendDocument", form, ct);
    }

    private Task<T> CallAsync<T>(string token, string method, object? body, CancellationToken ct) =>
        PostAsync<T>(token, method, JsonContent.Create(body ?? new { }, options: Json), ct);

    private async Task<T> PostAsync<T>(string token, string method, HttpContent content, CancellationToken ct)
    {
        if (!IsValidToken(token))
        {
            content.Dispose();
            throw new TelegramApiException(401, "Bot token has an invalid format", null);
        }

        // «./» обязательно: иначе «bot123:abc/getMe» читается как адрес со схемой «bot123».
        using var request = new HttpRequestMessage(HttpMethod.Post, $"./bot{token}/{method}") { Content = content };
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);

        TgResponse<T>? payload = null;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<TgResponse<T>>(Json, ct);
        }
        catch (JsonException)
        {
            // не JSON: например, ответ прокси
        }

        if (payload is { Ok: true, Result: not null })
            return payload.Result;
        throw new TelegramApiException(payload?.ErrorCode ?? (int)response.StatusCode,
            payload?.Description ?? response.ReasonPhrase ?? "Unknown error", payload?.Parameters?.RetryAfter);
    }

    [GeneratedRegex(@"^\d{1,20}:[A-Za-z0-9_-]{20,100}$")]
    private static partial Regex TokenPattern();
}

// Модели Bot API: только используемые поля. Имена в JSON — snake_case.

public sealed record TgResponse<T>(bool Ok, T? Result, string? Description, int? ErrorCode, TgResponseParameters? Parameters);

public sealed record TgResponseParameters(int? RetryAfter);

public sealed record TgUser(long Id, bool IsBot, string? FirstName, string? LastName, string? Username, string? LanguageCode);

public sealed record TgChat(long Id, string? Type);

public sealed record TgUpdate(long UpdateId, TgMessage? Message);

public sealed record TgMessage(
    long MessageId,
    TgUser? From,
    TgChat Chat,
    string? Text,
    string? Caption,
    JsonElement? Photo,
    JsonElement? Document,
    JsonElement? Sticker,
    JsonElement? Voice,
    JsonElement? Video,
    JsonElement? Audio,
    JsonElement? Animation,
    JsonElement? VideoNote)
{
    /// <summary>Что прислали вместо текста: вложения появятся вместе с хранилищем файлов.</summary>
    public string? AttachmentKind =>
        Photo is not null ? "photo"
        : Document is not null ? "file"
        : Sticker is not null ? "sticker"
        : Voice is not null ? "voice message"
        : Video is not null ? "video"
        : Audio is not null ? "audio"
        : Animation is not null ? "GIF"
        : VideoNote is not null ? "video message"
        : null;
}
