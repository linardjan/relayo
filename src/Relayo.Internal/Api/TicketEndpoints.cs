using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Relayo.Core.Auth;
using Relayo.Core;
using Relayo.Core.Conversations;
using Relayo.Core.Files;
using Relayo.Internal.Automation;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Channels.WhatsApp;
using Relayo.Internal.Messaging;
using Relayo.Internal.Realtime;

namespace Relayo.Internal.Api;

public sealed record ReplyRequest(Guid ChatId, string Text);

public sealed record SetAliasRequest(string Alias);

public sealed record SetGroupRequest(Guid? GroupId);

public sealed record SetTicketStatusRequest(TicketStatus Status);

public sealed record SetSubjectRequest(string? Subject);

/// <param name="UserId">Кому назначить; null — вернуть в очередь.</param>
public sealed record SetAssigneeRequest(Guid? UserId);

internal static class TicketEndpoints
{
    public static RouteGroupBuilder MapTicketEndpoints(this RouteGroupBuilder api)
    {
        var tickets = api.MapGroup("/tickets").RequireAuthorization();

        tickets.MapGet("/", (TicketService service, bool? closed, CancellationToken ct) =>
            service.ListAsync(open: closed != true, ct));

        // Раздел «Запросы»: все тикеты с фильтрами и постранично. status можно передать несколько раз.
        tickets.MapGet("/search", (TicketService service, ClaimsPrincipal user, [FromQuery] TicketStatus[]? status,
                Channel? channel, string? assignee, Guid? groupId, Guid? siteId, Guid? clientId, string? search,
                DateTimeOffset? createdFrom, DateTimeOffset? createdTo, DateTimeOffset? updatedFrom, DateTimeOffset? updatedTo,
                int? page, int? pageSize, CancellationToken ct) =>
            service.SearchAsync(new TicketFilter(status, channel, assignee, groupId, siteId, clientId, search, createdFrom,
                createdTo, updatedFrom, updatedTo), UserId(user), page ?? 1, pageSize ?? 25, ct));

        tickets.MapGet("/agents", (TicketService service, CancellationToken ct) => service.ListAgentsAsync(ct));

        // «Взять запрос»: самый старый свободный. 204 — свободных нет.
        tickets.MapPost("/take-next", async (ClaimsPrincipal user, TicketService service, OperatorNotifier operators,
            AutomationEvents automation, CancellationToken ct) =>
        {
            var summary = await service.TakeNextAsync(UserId(user), ct);
            if (summary is null)
                return Results.NoContent();
            await operators.TicketChangedAsync(summary);
            await automation.OperatorActionAsync(summary.Id, "ticket_assigned", TicketStatus.New, summary.Status);
            return Results.Ok(summary);
        });

        tickets.MapGet("/{id:guid}", (Guid id, TicketService service, CancellationToken ct) =>
            service.GetAsync(id, ct));

        // Ответ: JSON {chatId, text} или multipart/form-data (chatId, text, files) — с вложениями.
        tickets.MapPost("/{id:guid}/messages", async (Guid id, HttpRequest http, ClaimsPrincipal user,
            TicketService service, AttachmentLimits limits, WidgetEvents widgetEvents, TelegramOutbox telegram,
            WhatsAppOutbox whatsApp, OperatorNotifier operators, AutomationEvents automation, CancellationToken ct) =>
        {
            var request = await ReadReplyAsync(http, limits, ct);
            var before = await service.GetStatusAsync(id, ct);
            var reply = await service.ReplyAsync(id, request.ChatId, UserId(user), request.Text, request.Files, ct);
            if (reply.Telegram is { OperatorAlias: { } alias } delivery)
                await telegram.SendOperatorReplyAsync(delivery.BotId, delivery.ChatId, alias, delivery.Text, delivery.Files);
            if (reply.WhatsApp is { OperatorAlias: { } waAlias } waDelivery)
                await whatsApp.SendOperatorReplyAsync(waDelivery.ChannelId, waDelivery.ChatJid, waAlias, waDelivery.Text, waDelivery.Files);
            // Ответ видит и виджет: переписка в Telegram продолжает тот же чат.
            await widgetEvents.PublishAsync(reply.WidgetMessage, reply.Recipients);
            await operators.MessageCreatedAsync(id, reply.Message);
            await operators.TicketChangedAsync(reply.Ticket);
            await automation.OperatorActionAsync(id, "operator_replied", before, reply.Ticket.Status, reply.Message.Id);
            return reply.Message;
        }).WithMetadata(new RequestSizeLimitAttribute(
            ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<AttachmentLimits>().MaxBytes + FormFiles.FormOverheadBytes));

        // Внутренняя заметка: те же поля, что у ответа. Видят только сотрудники, клиенту ничего не уходит.
        tickets.MapPost("/{id:guid}/notes", async (Guid id, HttpRequest http, ClaimsPrincipal user,
            TicketService service, AttachmentLimits limits, OperatorNotifier operators, CancellationToken ct) =>
        {
            var request = await ReadReplyAsync(http, limits, ct);
            var note = await service.AddNoteAsync(id, request.ChatId, UserId(user), request.Text, request.Files, ct);
            await operators.MessageCreatedAsync(id, note);
            return note;
        }).WithMetadata(new RequestSizeLimitAttribute(
            ((IEndpointRouteBuilder)api).ServiceProvider.GetRequiredService<AttachmentLimits>().MaxBytes + FormFiles.FormOverheadBytes));

        tickets.MapGet("/groups", (TicketService service, CancellationToken ct) => service.ListGroupsAsync(ct));

        tickets.MapPut("/{id:guid}/group", async (Guid id, SetGroupRequest request, TicketService service,
            OperatorNotifier operators, CancellationToken ct) =>
        {
            var summary = await service.SetGroupAsync(id, request.GroupId, ct);
            await operators.TicketChangedAsync(summary);
            return summary;
        });

        tickets.MapPut("/{id:guid}/status", async (Guid id, SetTicketStatusRequest request, TicketService service,
            OperatorNotifier operators, AutomationEvents automation, CancellationToken ct) =>
        {
            var before = await service.GetStatusAsync(id, ct);
            var summary = await service.SetStatusAsync(id, request.Status, ct);
            await operators.TicketChangedAsync(summary);
            if (before != summary.Status)
            {
                // Закрытие — ещё и событие ticket_closed; status_changed правила получают в обоих случаях один раз.
                if (summary.Status == TicketStatus.Closed)
                    await automation.OperatorActionAsync(id, "ticket_closed", before, summary.Status);
                else
                    await automation.OperatorActionAsync(id, "status_changed", null, summary.Status);
            }
            return summary;
        });

        tickets.MapPut("/{id:guid}/subject", async (Guid id, SetSubjectRequest request, TicketService service,
            OperatorNotifier operators, CancellationToken ct) =>
        {
            var summary = await service.SetSubjectAsync(id, request.Subject, ct);
            await operators.TicketChangedAsync(summary);
            return summary;
        });

        // Назначить другому сотруднику могут администратор и супервизор; оператор — только себе или вернуть свой в очередь.
        tickets.MapPut("/{id:guid}/assignee", async (Guid id, SetAssigneeRequest request, ClaimsPrincipal user,
            TicketService service, OperatorNotifier operators, AutomationEvents automation, CancellationToken ct) =>
        {
            var me = UserId(user);
            var canAssignOthers = user.IsInRole(nameof(Relayo.Core.Users.UserRole.Admin))
                                  || user.IsInRole(nameof(Relayo.Core.Users.UserRole.Supervisor));
            if (!canAssignOthers)
            {
                var current = await service.GetSummaryAsync(id, ct);
                var allowed = request.UserId == me || (request.UserId is null && current.AssigneeId == me);
                if (!allowed)
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "Only administrators and supervisors can assign tickets to other agents");
            }
            var before = await service.GetStatusAsync(id, ct);
            var summary = await service.SetAssigneeAsync(id, request.UserId, ct);
            await operators.TicketChangedAsync(summary);
            if (request.UserId is not null)
                await automation.OperatorActionAsync(id, "ticket_assigned", before, summary.Status);
            else if (before != summary.Status)
                await automation.OperatorActionAsync(id, "status_changed", null, summary.Status);
            return Results.Ok(summary);
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

    private sealed record Reply(Guid ChatId, string Text, IReadOnlyList<FileUpload> Files);

    private static async Task<Reply> ReadReplyAsync(HttpRequest http, AttachmentLimits limits, CancellationToken ct)
    {
        if (!http.HasFormContentType)
        {
            var json = await http.ReadFromJsonAsync<ReplyRequest>(ct)
                       ?? throw new DomainException(ErrorCodes.InvalidInput, "Request body is empty");
            return new Reply(json.ChatId, json.Text ?? string.Empty, []);
        }
        if (http.ContentLength > limits.MaxBytes + FormFiles.FormOverheadBytes)
            throw new DomainException(ErrorCodes.AttachmentTooLarge,
                $"Files of one message must be up to {limits.MaxBytes / (1024 * 1024)} MB in total");
        var form = await http.ReadFormAsync(ct);
        if (!Guid.TryParse(form["chatId"], out var chatId))
            throw new DomainException(ErrorCodes.InvalidInput, "chatId is required");
        return new Reply(chatId, form["text"].ToString(), await FormFiles.ReadAsync(form.Files, limits.MaxBytes, ct));
    }

    private static Guid UserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(TokenService.SubjectClaim)!);
}
