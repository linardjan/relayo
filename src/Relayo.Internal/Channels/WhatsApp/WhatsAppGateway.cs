using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relayo.Core;

namespace Relayo.Internal.Channels.WhatsApp;

/// <param name="Url">Адрес шлюза WhatsApp (src/Relayo.WhatsApp) во внутренней сети.</param>
/// <param name="Key">Общий ключ: им шлюз и внутренний сервис подписывают вызовы друг друга.</param>
/// <remarks>
/// Номера здесь не задаются: их подключают в админке («Channels → Add channel → WhatsApp») сканированием QR-кода.
/// </remarks>
public sealed record WhatsAppGatewaySettings(Uri Url, string Key);

/// <summary>Состояние подключения номера в шлюзе. Status: starting, qr, connected, disconnected.</summary>
public sealed record WhatsAppSession(
    string Status,
    string? Qr,
    DateTimeOffset? QrExpiresAt,
    string? Phone,
    string? Name,
    string? Reason);

/// <summary>Шлюз ответил, что номер не подключён: отправку стоит повторить позже.</summary>
public sealed class WhatsAppNotConnectedException(Guid channelId)
    : Exception($"WhatsApp channel {channelId} is not connected in the gateway");

/// <summary>Вызовы шлюза WhatsApp. Ключ сессий и протокол WhatsApp Web живут только в шлюзе.</summary>
public sealed class WhatsAppGateway(IHttpClientFactory http, ILogger<WhatsAppGateway> logger, WhatsAppGatewaySettings? settings = null)
{
    public const string HttpClientName = "whatsapp-gateway";
    public const string KeyHeader = "X-Relayo-Gateway-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsConfigured => settings is not null;

    public Task<WhatsAppSession> GetSessionAsync(Guid channelId, CancellationToken ct) =>
        CallAsync(HttpMethod.Get, Path(channelId), null, ct);

    /// <summary>Начать вход по QR-коду (или восстановить соединение, если вход уже был).</summary>
    public Task<WhatsAppSession> StartAsync(Guid channelId, CancellationToken ct) =>
        CallAsync(HttpMethod.Put, Path(channelId), null, ct);

    /// <summary>Выйти из WhatsApp: устройство пропадёт из «Связанных устройств», ключи сессии стираются.</summary>
    public async Task LogoutAsync(Guid channelId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Delete, Path(channelId), null, ct);

    public async Task SendTextAsync(Guid channelId, string chatJid, string text, CancellationToken ct) =>
        await SendMessageAsync(channelId, new { to = chatJid, text }, ct);

    public async Task SendFileAsync(Guid channelId, string chatJid, byte[] content, string fileName, string contentType,
        string? caption, CancellationToken ct) =>
        await SendMessageAsync(channelId, new
        {
            to = chatJid,
            file = new { fileName, contentType, content = Convert.ToBase64String(content), caption },
        }, ct);

    private async Task SendMessageAsync(Guid channelId, object body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, Path(channelId) + "/messages", body, ct, allowConflict: true);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new WhatsAppNotConnectedException(channelId);
    }

    private async Task<WhatsAppSession> CallAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, ct);
        return await response.Content.ReadFromJsonAsync<WhatsAppSession>(Json, ct)
               ?? throw Unavailable("empty response");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        bool allowConflict = false)
    {
        if (settings is null)
            throw new DomainException(ErrorCodes.WhatsAppGatewayNotConfigured,
                "The WhatsApp gateway is not configured: set WHATSAPP_GATEWAY_URL and WHATSAPP_GATEWAY_KEY");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(KeyHeader, settings.Key);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("WhatsApp gateway is unreachable: {Error}", ex.Message);
            throw Unavailable("unreachable");
        }
        if (response.IsSuccessStatusCode || (allowConflict && response.StatusCode == HttpStatusCode.Conflict))
            return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        logger.LogWarning("WhatsApp gateway responded {Status} to {Method} {Path}", status, method, path);
        throw Unavailable($"status {status}");
    }

    private static string Path(Guid channelId) => $"sessions/{channelId:D}";

    private static DomainException Unavailable(string details) =>
        new(ErrorCodes.WhatsAppGatewayUnavailable, $"The WhatsApp gateway is not available ({details}). Check that it is running");
}
