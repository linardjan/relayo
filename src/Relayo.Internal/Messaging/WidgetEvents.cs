using Relayo.Contracts.Messages;
using Relayo.Core.Conversations;
using Wolverine;

namespace Relayo.Internal.Messaging;

/// <summary>Отправляет сообщения в открытые виджеты через публичные сервисы (RabbitMQ, fanout).</summary>
public sealed class WidgetEvents(IMessageBus bus)
{
    public ValueTask PublishAsync(MessageView message, WidgetRecipients recipients) =>
        bus.PublishAsync(new WidgetMessagePublished(ToDto(message), recipients.VisitorIds, recipients.VerifiedClientId));

    public static WidgetMessageDto ToDto(MessageView m) => new(
        m.Id, m.ChatId, AuthorCode(m.Author), m.AuthorName, m.Text, m.CreatedAt, m.ClientMessageId,
        m.Attachments?.Select(a => new WidgetAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size, a.Url)).ToList());

    public static string AuthorCode(MessageAuthor author) => author switch
    {
        MessageAuthor.Client => "client",
        MessageAuthor.Operator => "operator",
        _ => "system",
    };
}
