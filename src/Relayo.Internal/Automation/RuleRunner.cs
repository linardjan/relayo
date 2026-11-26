using Relayo.Core.Automation;
using Relayo.Core.Conversations;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Channels.WhatsApp;
using Relayo.Internal.Messaging;
using Relayo.Internal.Realtime;
using Wolverine;

namespace Relayo.Internal.Automation;

/// <summary>Событие для правил. Идёт через durable-очередь «rules»: не теряется при перезапуске.</summary>
public sealed record RuleEventOccurred(Guid TicketId, string Kind, Guid? MessageId = null, bool TicketCreated = false);

/// <summary>Сработал таймер «не ответил N минут» (отложенное сообщение Wolverine, хранится в БД).</summary>
public sealed record RuleTimerElapsed(Guid TicketId, Guid RuleId, string Type, int Minutes, Guid MessageId);

/// <summary>Публикует события для правил из обработчиков API и каналов.</summary>
public sealed class AutomationEvents(IMessageBus bus)
{
    public const string QueueName = "rules";

    public ValueTask ClientMessageAsync(PostedVisitorMessage posted) =>
        bus.PublishAsync(new RuleEventOccurred(posted.TicketId, "client_message", posted.Message.Id, posted.TicketCreated));

    /// <summary>Действие оператора; если оно поменяло статус, правила получают ещё и status_changed.</summary>
    public async Task OperatorActionAsync(Guid ticketId, string kind, TicketStatus? before, TicketStatus after, Guid? messageId = null)
    {
        await bus.PublishAsync(new RuleEventOccurred(ticketId, kind, messageId));
        if (before is not null && before != after)
            await bus.PublishAsync(new RuleEventOccurred(ticketId, "status_changed"));
    }
}

/// <summary>
/// Выполняет правила и доводит результат до людей: автоответы — в виджет и Telegram,
/// изменения тикета — операторам. Свой scope на каждый запуск: обработчик Wolverine не держит DbContext.
/// </summary>
public sealed class RuleRunner(IServiceScopeFactory scopes)
{
    public async Task<RuleRunResult> RunAsync(RuleEventOccurred evt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<RuleEngine>()
            .RunAsync(new RuleEventInput(evt.TicketId, evt.Kind, evt.MessageId, evt.TicketCreated), ct);
        await DeliverAsync(scope.ServiceProvider, result, ct);
        return result;
    }

    public async Task RunTimerAsync(RuleTimerElapsed timer, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<RuleEngine>()
            .RunTimerAsync(new RuleTimer(timer.RuleId, timer.Type, timer.Minutes, timer.MessageId), timer.TicketId, ct);
        await DeliverAsync(scope.ServiceProvider, result, ct);
    }

    private static async Task DeliverAsync(IServiceProvider services, RuleRunResult result, CancellationToken ct)
    {
        if (!result.TicketChanged && result.Messages.Count == 0)
            return;
        var widgets = services.GetRequiredService<WidgetEvents>();
        var operators = services.GetRequiredService<OperatorNotifier>();
        var telegram = services.GetRequiredService<TelegramOutbox>();
        var whatsApp = services.GetRequiredService<WhatsAppOutbox>();
        foreach (var message in result.Messages)
        {
            if (message.Telegram is { } delivery)
                await telegram.SendTextAsync(delivery.BotId, delivery.ChatId, delivery.Text);
            if (message.WhatsApp is { } waDelivery)
                await whatsApp.SendTextAsync(waDelivery.ChannelId, waDelivery.ChatJid, waDelivery.Text);
            await widgets.PublishAsync(message.Widget, message.Recipients);
            await operators.MessageCreatedAsync(result.TicketId, message.Operator);
        }
        await operators.TicketChangedAsync(await services.GetRequiredService<TicketService>().GetSummaryAsync(result.TicketId, ct));
    }
}

/// <summary>Обработчик очереди правил (Wolverine).</summary>
public sealed class RuleEventHandler(RuleRunner runner)
{
    public async Task Handle(RuleEventOccurred evt, IMessageContext context, CancellationToken ct)
    {
        var result = await runner.RunAsync(evt, ct);
        // Таймеры «не ответил N минут»: проверим через N минут, был ли ответ.
        foreach (var timer in result.Timers)
            await context.ScheduleAsync(new RuleTimerElapsed(evt.TicketId, timer.RuleId, timer.Type, timer.Minutes, timer.MessageId),
                TimeSpan.FromMinutes(timer.Minutes));
    }

    public Task Handle(RuleTimerElapsed timer, CancellationToken ct) => runner.RunTimerAsync(timer, ct);
}
