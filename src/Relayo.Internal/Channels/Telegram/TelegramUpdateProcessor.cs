using Relayo.Core.Channels;
using Relayo.Core.Conversations;
using Relayo.Internal.Messaging;

namespace Relayo.Internal.Channels.Telegram;

/// <summary>
/// Обработка одного обновления Telegram. Одинакова для webhook и для long polling.
/// Повторно присланное сообщение не создаёт дубль: у него тот же message_id.
/// </summary>
public sealed class TelegramUpdateProcessor(
    TelegramConversationService telegram,
    ConversationEvents events,
    TelegramOutbox outbox,
    ILogger<TelegramUpdateProcessor> logger)
{
    public async Task ProcessAsync(TelegramBot bot, TgUpdate update, CancellationToken ct)
    {
        // Работаем только с личными чатами: группы и каналы боту поддержки не нужны.
        if (update.Message is not { From: { IsBot: false } from } message || message.Chat.Type != "private")
            return;

        var sender = new TelegramSender(message.Chat.Id, from.Id, from.FirstName, from.LastName, from.Username,
            from.LanguageCode);

        if (message.Text is { } command && TryParseStart(command, out var payload))
        {
            var start = await telegram.StartAsync(bot, sender, payload, ct);
            await outbox.SendTextAsync(bot.Id, sender.ChatId, start.Result switch
            {
                TelegramStartResult.Linked => TelegramTexts.Linked,
                TelegramStartResult.LinkExpired => TelegramTexts.LinkExpired,
                _ => TelegramTexts.Greeting,
            });
            if (start.SystemMessage is { } system)
                await events.ClientMessagePostedAsync(system, Channel.Telegram, ct);
            logger.LogInformation("Telegram /start for bot {BotId}: {Result}", bot.Id, start.Result);
            return;
        }

        var text = message.Text;
        if (text is null)
        {
            // Вложения появятся вместе с хранилищем файлов. Пока оператор видит, что клиент что-то прислал.
            var kind = message.AttachmentKind ?? "message";
            text = string.IsNullOrWhiteSpace(message.Caption)
                ? $"[The client sent a {kind}: attachments are not supported yet]"
                : $"{message.Caption}\n[The client sent a {kind}: attachments are not supported yet]";
            await outbox.SendTextAsync(bot.Id, sender.ChatId, TelegramTexts.AttachmentsNotSupported);
        }

        var posted = await telegram.ReceiveAsync(bot, sender, message.MessageId, text, ct);
        if (!posted.Duplicate)
            await events.ClientMessagePostedAsync(posted, Channel.Telegram, ct);
    }

    /// <summary>/start, /start &lt;payload&gt; или /start@BotName &lt;payload&gt;.</summary>
    internal static bool TryParseStart(string text, out string? payload)
    {
        payload = null;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(TelegramConversationService.StartCommand, StringComparison.Ordinal))
            return false;
        var rest = trimmed[TelegramConversationService.StartCommand.Length..];
        if (rest.Length > 0 && rest[0] != ' ' && rest[0] != '@')
            return false;
        if (rest.StartsWith('@'))
        {
            var space = rest.IndexOf(' ');
            rest = space < 0 ? string.Empty : rest[space..];
        }
        payload = rest.Trim() is { Length: > 0 } p ? p : null;
        return true;
    }
}
