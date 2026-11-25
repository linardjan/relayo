using Relayo.Core.Conversations;
using Relayo.Internal.Automation;
using Relayo.Internal.Realtime;

namespace Relayo.Internal.Messaging;

/// <summary>
/// Новое сообщение со стороны клиента: его сразу видят открытые виджеты клиента (другие вкладки, устройства)
/// и операторы. Используется для виджета и для мессенджеров.
/// </summary>
public sealed class ConversationEvents(
    WidgetEvents widgetEvents,
    TicketService tickets,
    OperatorNotifier operators,
    AutomationEvents automation)
{
    public async Task ClientMessagePostedAsync(PostedVisitorMessage posted, Channel channel, CancellationToken ct)
    {
        await widgetEvents.PublishAsync(posted.Message, posted.Recipients);
        var summary = await tickets.GetSummaryAsync(posted.TicketId, ct);
        await operators.TicketChangedAsync(summary);
        await operators.MessageCreatedAsync(posted.TicketId, new OperatorMessageView(
            posted.Message.Id, posted.Message.ChatId, posted.Message.Author, null, posted.Message.Text,
            posted.Message.CreatedAt, channel));
        // Сообщение клиента запускает правила; системные пометки («перешёл в Telegram») — нет.
        if (posted.Message.Author == MessageAuthor.Client)
            await automation.ClientMessageAsync(posted);
    }
}
