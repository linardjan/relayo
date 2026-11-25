using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relayo.Core.Clients;
using Relayo.Core.Conversations;
using Relayo.Core.Data;

namespace Relayo.Core.Automation;

/// <summary>Событие для правил. Kind: client_message, operator_replied, ticket_assigned, ticket_closed, status_changed.</summary>
public sealed record RuleEventInput(Guid TicketId, string Kind, Guid? MessageId = null, bool TicketCreated = false);

/// <summary>Автоответ правила: его нужно показать операторам, доставить в виджет и, если нужно, в Telegram.</summary>
public sealed record AutomatedMessage(
    MessageView Widget,
    OperatorMessageView Operator,
    WidgetRecipients Recipients,
    TelegramDelivery? Telegram);

/// <summary>Таймер «не ответил N минут»: проверить правило через Minutes после сообщения MessageId.</summary>
public sealed record RuleTimer(Guid RuleId, string Type, int Minutes, Guid MessageId);

public sealed record RuleRunResult(Guid TicketId, bool TicketChanged, IReadOnlyList<AutomatedMessage> Messages,
    IReadOnlyList<RuleTimer> Timers)
{
    public static RuleRunResult Nothing(Guid ticketId) => new(ticketId, false, [], []);
}

/// <summary>
/// Движок правил (п. 3.6 ТЗ): на событие проверяет включённые правила по порядку, выполняет действия
/// подходящих и пишет журнал. Действия правил не порождают новых событий для правил — так правила
/// не могут зациклиться. Автоответов не больше <see cref="MaxAutoRepliesPerHour"/> на тикет в час.
/// </summary>
public sealed class RuleEngine(
    RelayoDbContext db,
    WidgetConversationService widgets,
    TicketService tickets,
    TimeProvider time,
    ILogger<RuleEngine> logger)
{
    public const int MaxAutoRepliesPerHour = 3;
    public const string NoOperatorReply = "no_operator_reply";
    public const string NoClientReply = "no_client_reply";

    public async Task<RuleRunResult> RunAsync(RuleEventInput evt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockTicketAsync(evt.TicketId, ct);
        if (ticket is null)
            return RuleRunResult.Nothing(evt.TicketId);

        // Одно событие может означать несколько: первое сообщение клиента — это ещё и новый тикет.
        var events = new List<string> { evt.Kind };
        if (evt.Kind == "client_message" && evt.TicketCreated)
        {
            events.Add("ticket_created");
            if (await db.Tickets.AnyAsync(t => t.ClientId == ticket.ClientId && t.Id != ticket.Id && t.MergedIntoId == null, ct))
                events.Add("returning_client");
        }

        var rules = await ActiveRulesAsync(ct);
        var timers = TimersFor(rules, evt);

        var context = await BuildContextAsync(ticket, evt.MessageId, ct);
        var run = new Run(ticket, context);
        foreach (var rule in rules)
        {
            var triggers = rule.Definition.WhenAll.Concat(rule.Definition.WhenAny).Select(t => t.Type).ToHashSet();
            var trigger = events.FirstOrDefault(triggers.Contains);
            if (trigger is null || !RuleMatcher.Matches(rule, context.Conditions))
                continue;
            await ApplyAsync(rule, trigger, run, ct);
            if (rule.StopProcessing)
                break;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new RuleRunResult(ticket.Id, run.TicketChanged, run.Messages, timers);
    }

    /// <summary>
    /// Таймер сработал. Правило применяется, только если ответа так и не было и этот таймер — первый
    /// для текущего ожидания (клиент мог написать несколько сообщений подряд).
    /// </summary>
    public async Task<RuleRunResult> RunTimerAsync(RuleTimer timer, Guid ticketId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockTicketAsync(ticketId, ct);
        var rule = await db.Rules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == timer.RuleId, ct);
        if (ticket is null || !ticket.IsOpen || rule is not { IsEnabled: true, IsManual: false }
            || !rule.Definition.WhenAll.Concat(rule.Definition.WhenAny).Any(t => t.Type == timer.Type && t.Minutes == timer.Minutes))
            return RuleRunResult.Nothing(ticketId);

        var chatIds = ticket.Chats.Select(c => c.Id).ToList();
        var messages = db.Messages.AsNoTracking().Where(m => chatIds.Contains(m.ChatId));
        var lastOperatorAt = await messages.Where(m => m.Author == MessageAuthor.Operator && m.OperatorId != null)
            .MaxAsync(m => (DateTimeOffset?)m.CreatedAt, ct);

        bool due;
        if (timer.Type == NoOperatorReply)
        {
            // Первое сообщение клиента после последнего ответа оператора — то, от которого отсчитан таймер.
            var firstUnanswered = await messages
                .Where(m => m.Author == MessageAuthor.Client && (lastOperatorAt == null || m.CreatedAt > lastOperatorAt))
                .OrderBy(m => m.CreatedAt).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
            due = firstUnanswered == timer.MessageId;
        }
        else
        {
            var lastOperator = await messages.Where(m => m.Author == MessageAuthor.Operator && m.OperatorId != null)
                .OrderByDescending(m => m.CreatedAt).Select(m => new { m.Id, m.CreatedAt }).FirstOrDefaultAsync(ct);
            due = lastOperator?.Id == timer.MessageId
                  && !await messages.AnyAsync(m => m.Author == MessageAuthor.Client && m.CreatedAt > lastOperator.CreatedAt, ct);
        }
        if (!due)
            return RuleRunResult.Nothing(ticketId);

        var context = await BuildContextAsync(ticket, null, ct);
        var run = new Run(ticket, context);
        if (RuleMatcher.Matches(rule, context.Conditions))
            await ApplyAsync(rule, $"{timer.Type} {timer.Minutes} min", run, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new RuleRunResult(ticket.Id, run.TicketChanged, run.Messages, []);
    }

    private async Task ApplyAsync(Rule rule, string trigger, Run run, CancellationToken ct)
    {
        var done = new List<string>();
        var skipped = new List<string>();
        var now = time.GetUtcNow();
        var ticket = run.Ticket;

        foreach (var action in rule.Definition.Actions)
        {
            switch (action.Type)
            {
                case "send_message":
                    if (await AutoRepliesLastHourAsync(ticket, ct) + run.Messages.Count >= MaxAutoRepliesPerHour)
                    {
                        skipped.Add($"send_message: limit of {MaxAutoRepliesPerHour} auto-replies per hour");
                        break;
                    }
                    var text = RuleMatcher.Render(action.Value ?? string.Empty, run.Context.ClientName,
                        run.Context.AssigneeAlias, ticket.Number);
                    if (text.Length == 0)
                    {
                        skipped.Add("send_message: empty text");
                        break;
                    }
                    run.Messages.Add(await SendAsync(ticket, run.Context.ChatId, text, now, ct));
                    done.Add("send_message");
                    break;

                case "set_status" when Enum.TryParse<TicketStatus>(action.Value, out var status):
                    ticket.SetStatus(status, now);
                    run.TicketChanged = true;
                    done.Add($"set_status {status}");
                    break;

                case "assign" when Guid.TryParse(action.Value, out var userId):
                    if (await db.Users.AnyAsync(u => u.Id == userId && !u.IsBlocked, ct) && ticket.IsOpen)
                    {
                        ticket.Assign(userId, now);
                        run.TicketChanged = true;
                        done.Add("assign");
                    }
                    else
                        skipped.Add("assign: agent is blocked or removed, or the ticket is closed");
                    break;

                case "unassign":
                    ticket.Unassign(now);
                    run.TicketChanged = true;
                    done.Add("unassign");
                    break;

                case "close_ticket":
                    if (ticket.IsOpen)
                    {
                        ticket.Close(now);
                        run.TicketChanged = true;
                        done.Add("close_ticket");
                    }
                    break;

                default:
                    skipped.Add($"{action.Type}: not supported");
                    break;
            }
        }

        var details = (done.Count > 0 ? "Done: " + string.Join(", ", done) : "Nothing done")
                      + (skipped.Count > 0 ? ". Skipped: " + string.Join("; ", skipped) : string.Empty);
        db.RuleExecutions.Add(new RuleExecution(rule.Id, rule.Name, ticket.Id, ticket.Number, trigger, done.Count > 0, details, now));
        logger.LogInformation("Rule {Rule} on ticket #{Ticket} ({Trigger}): {Details}", rule.Name, ticket.Number, trigger, details);
    }

    /// <summary>Автоответ уходит в тот же канал, что и ответ оператора: откуда клиент написал последним.</summary>
    private async Task<AutomatedMessage> SendAsync(Ticket ticket, Guid? chatId, string text, DateTimeOffset now,
        CancellationToken ct)
    {
        var chat = ticket.Chats.FirstOrDefault(c => c.Id == chatId) ?? ticket.Chats.MaxBy(c => c.LastMessageAt)!;
        var (channel, telegram) = await tickets.ReplyRouteAsync(chat.Id, ct);
        var message = Message.FromAutomation(chat, text, channel, now);
        db.Messages.Add(message);
        chat.OnMessage(now);
        await db.SaveChangesAsync(ct);

        return new AutomatedMessage(
            WidgetConversationService.ToView(message, null),
            new OperatorMessageView(message.Id, chat.Id, message.Author, TicketService.AutoReplyName, message.Text,
                message.CreatedAt, message.Channel, Automated: true),
            await widgets.GetRecipientsAsync(chat, ct),
            telegram is { } t ? new TelegramDelivery(t.BotId, t.ChatId, null, text) : null);
    }

    private async Task<int> AutoRepliesLastHourAsync(Ticket ticket, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-1);
        var chatIds = ticket.Chats.Select(c => c.Id).ToList();
        return await db.Messages.CountAsync(m => chatIds.Contains(m.ChatId) && m.CreatedAt >= since
                                                 && m.Author == MessageAuthor.Operator && m.OperatorId == null, ct);
    }

    private async Task<Ticket?> LockTicketAsync(Guid ticketId, CancellationToken ct)
    {
        // Правила одного тикета выполняются по очереди: иначе два события подряд могли бы оба отправить автоответ.
        await db.LockAsync("rules:" + ticketId, ct);
        return await db.Tickets.Include(t => t.Chats).SingleOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    private async Task<List<Rule>> ActiveRulesAsync(CancellationToken ct) =>
        await db.Rules.AsNoTracking().Where(r => r.IsEnabled && !r.IsManual)
            .OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ToListAsync(ct);

    /// <summary>Таймеры заводятся на сообщение клиента (ждём оператора) и на ответ оператора (ждём клиента).</summary>
    private static List<RuleTimer> TimersFor(IEnumerable<Rule> rules, RuleEventInput evt)
    {
        var type = evt.Kind switch
        {
            "client_message" => NoOperatorReply,
            "operator_replied" => NoClientReply,
            _ => null,
        };
        if (type is null || evt.MessageId is not { } messageId)
            return [];
        return rules
            .SelectMany(r => r.Definition.WhenAll.Concat(r.Definition.WhenAny)
                .Where(t => t.Type == type && t.Minutes > 0)
                .Select(t => new RuleTimer(r.Id, type, t.Minutes!.Value, messageId)))
            .Distinct()
            .ToList();
    }

    private async Task<Context> BuildContextAsync(Ticket ticket, Guid? eventMessageId, CancellationToken ct)
    {
        var chatIds = ticket.Chats.Select(c => c.Id).ToList();
        var eventMessage = eventMessageId is { } id
            ? await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
            : null;
        // Канал клиента — по его последнему сообщению; текст — сообщение события или последнее сообщение клиента.
        var lastClient = eventMessage?.Author == MessageAuthor.Client
            ? eventMessage
            : await db.Messages.AsNoTracking()
                .Where(m => chatIds.Contains(m.ChatId) && m.Author == MessageAuthor.Client)
                .OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync(ct);

        var identity = lastClient?.VisitorIdentityId is { } identityId
            ? await db.ClientIdentities.AsNoTracking().SingleOrDefaultAsync(i => i.Id == identityId, ct)
            : null;
        var channelKey = identity switch
        {
            { Type: IdentityType.Visitor, WidgetId: { } widgetId } => $"widget:{widgetId}",
            { Type: IdentityType.Telegram, ChannelId: { } botId } => $"telegram:{botId}",
            _ => null,
        };

        var client = await db.Clients.AsNoTracking().SingleAsync(c => c.Id == ticket.ClientId, ct);
        var alias = ticket.AssigneeId is { } assignee
            ? await db.Users.Where(u => u.Id == assignee).Select(u => u.Alias).SingleOrDefaultAsync(ct)
            : null;

        return new Context(
            new RuleContext(lastClient?.Channel, channelKey, client.SiteId, ticket.Status, ticket.AssigneeId,
                client.IsVerified, (eventMessage ?? lastClient)?.Text, client.Locale),
            (eventMessage ?? lastClient)?.ChatId,
            client.DisplayName ?? client.ExternalClientId,
            alias);
    }

    private sealed record Context(RuleContext Conditions, Guid? ChatId, string? ClientName, string? AssigneeAlias);

    private sealed class Run(Ticket ticket, Context context)
    {
        public Ticket Ticket { get; } = ticket;

        public Context Context { get; } = context;

        public bool TicketChanged { get; set; }

        public List<AutomatedMessage> Messages { get; } = [];
    }
}
