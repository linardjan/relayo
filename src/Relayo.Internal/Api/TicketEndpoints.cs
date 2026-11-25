using System.Security.Claims;
using Relayo.Core.Auth;
using Relayo.Core.Conversations;
using Relayo.Internal.Automation;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Messaging;
using Relayo.Internal.Realtime;

namespace Relayo.Internal.Api;

public sealed record ReplyRequest(Guid ChatId, string Text);

public sealed record SetAliasRequest(string Alias);

internal static class TicketEndpoints
{
    public static RouteGroupBuilder MapTicketEndpoints(this RouteGroupBuilder api)
    {
        var tickets = api.MapGroup("/tickets").RequireAuthorization();

        tickets.MapGet("/", (TicketService service, bool? closed, CancellationToken ct) =>
            service.ListAsync(open: closed != true, ct));

        tickets.MapGet("/{id:guid}", (Guid id, TicketService service, CancellationToken ct) =>
            service.GetAsync(id, ct));

        tickets.MapPost("/{id:guid}/messages", async (Guid id, ReplyRequest request, ClaimsPrincipal user,
            TicketService service, WidgetEvents widgetEvents, TelegramOutbox telegram, OperatorNotifier operators,
            AutomationEvents automation, CancellationToken ct) =>
        {
            var before = await service.GetStatusAsync(id, ct);
            var reply = await service.ReplyAsync(id, request.ChatId, UserId(user), request.Text, ct);
            if (reply.Telegram is { OperatorAlias: { } alias } delivery)
                await telegram.SendOperatorReplyAsync(delivery.BotId, delivery.ChatId, alias, delivery.Text);
            // Ответ видит и виджет: переписка в Telegram продолжает тот же чат.
            await widgetEvents.PublishAsync(reply.WidgetMessage, reply.Recipients);
            await operators.MessageCreatedAsync(id, reply.Message);
            await operators.TicketChangedAsync(reply.Ticket);
            await automation.OperatorActionAsync(id, "operator_replied", before, reply.Ticket.Status, reply.Message.Id);
            return reply.Message;
        });

        tickets.MapPost("/{id:guid}/assign", async (Guid id, ClaimsPrincipal user, TicketService service,
            OperatorNotifier operators, AutomationEvents automation, CancellationToken ct) =>
        {
            var before = await service.GetStatusAsync(id, ct);
            var summary = await service.AssignAsync(id, UserId(user), ct);
            await operators.TicketChangedAsync(summary);
            await automation.OperatorActionAsync(id, "ticket_assigned", before, summary.Status);
            return summary;
        });

        tickets.MapPost("/{id:guid}/close", async (Guid id, TicketService service, OperatorNotifier operators,
            AutomationEvents automation, CancellationToken ct) =>
        {
            var before = await service.GetStatusAsync(id, ct);
            var summary = await service.CloseAsync(id, ct);
            await operators.TicketChangedAsync(summary);
            if (before != TicketStatus.Closed)
                await automation.OperatorActionAsync(id, "ticket_closed", before, summary.Status);
            return summary;
        });

        api.MapPut("/me/alias", async (SetAliasRequest request, ClaimsPrincipal user, TicketService service,
            CancellationToken ct) => UserDto.From(await service.SetAliasAsync(UserId(user), request.Alias, ct)))
            .RequireAuthorization();

        return api;
    }

    private static Guid UserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(TokenService.SubjectClaim)!);
}
