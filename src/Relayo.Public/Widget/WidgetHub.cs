using Microsoft.AspNetCore.SignalR;
using Relayo.Contracts.Messages;
using Relayo.Public.Internal;

namespace Relayo.Public.Widget;

/// <summary>
/// Realtime для виджета: сюда приходят ответы операторов и сообщения из других вкладок.
/// Подключение: /hubs/widget?widgetId=...&amp;access_token=&lt;токен посетителя&gt;
/// (или токен в заголовке Authorization: Bearer).
/// Посетитель попадает в группу v:{visitorId}, а подтверждённый — ещё и в c:{clientId}.
/// </summary>
public sealed class WidgetHub(WidgetConfigCache configs, OriginPolicy origins, InternalApi api) : Hub
{
    public const string Path = "/hubs/widget";

    public static string VisitorGroup(Guid visitorId) => $"v:{visitorId}";

    public static string ClientGroup(Guid clientId) => $"c:{clientId}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext()!;
        var widgetId = http.Request.Query["widgetId"].ToString();
        // Браузер передаёт токен в query (WebSocket не умеет заголовки), другие клиенты — в Authorization.
        var token = http.Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(token)
            && http.Request.Headers.Authorization.ToString() is { } auth
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = auth["Bearer ".Length..].Trim();

        try
        {
            await WidgetEndpoints.GetCheckedConfigAsync(widgetId, http, configs, origins, Context.ConnectionAborted);
            var session = await api.StartVisitorAsync(widgetId, token, createIfMissing: false, Context.ConnectionAborted);

            await Groups.AddToGroupAsync(Context.ConnectionId, VisitorGroup(Guid.Parse(session.VisitorId)));
            if (session.Verified && Guid.TryParse(session.ClientId, out var clientId))
                await Groups.AddToGroupAsync(Context.ConnectionId, ClientGroup(clientId));
        }
        catch (WidgetApiException)
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }
}

/// <summary>Событие от внутреннего сервиса: рассылаем сообщение подключённым посетителям.</summary>
public sealed class WidgetMessagePublishedHandler(IHubContext<WidgetHub> hub)
{
    public Task Handle(WidgetMessagePublished published)
    {
        var groups = published.VisitorIds.Select(WidgetHub.VisitorGroup).ToList();
        if (published.VerifiedClientId is { } clientId)
            groups.Add(WidgetHub.ClientGroup(clientId));
        return groups.Count == 0
            ? Task.CompletedTask
            : hub.Clients.Groups(groups).SendAsync("message", published.Message);
    }
}
