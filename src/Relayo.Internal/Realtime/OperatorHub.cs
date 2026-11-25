using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Relayo.Core.Conversations;

namespace Relayo.Internal.Realtime;

/// <summary>
/// Realtime для админки: новые сообщения и изменения тикетов.
/// Пока все события получают все операторы; разделение по отделам появится вместе с отделами.
/// При нескольких экземплярах события между ними передаёт Redis backplane (REDIS_HOST).
/// </summary>
[Authorize]
public sealed class OperatorHub : Hub
{
    public const string Path = "/hubs/operators";
}

public sealed class OperatorNotifier(IHubContext<OperatorHub> hub)
{
    public Task TicketChangedAsync(TicketSummary ticket) =>
        hub.Clients.All.SendAsync("ticketChanged", ticket);

    public Task MessageCreatedAsync(Guid ticketId, OperatorMessageView message) =>
        hub.Clients.All.SendAsync("messageCreated", new { ticketId, message });
}
