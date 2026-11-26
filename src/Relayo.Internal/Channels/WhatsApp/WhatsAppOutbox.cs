using Microsoft.EntityFrameworkCore;
using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Core.Data;
using Relayo.Core.Files;
using Relayo.Core.Sites;
using Wolverine;

namespace Relayo.Internal.Channels.WhatsApp;

/// <summary>Файл для отправки в WhatsApp: лежит в S3, в очереди — только ссылка на него.</summary>
public sealed record WhatsAppFile(string StorageKey, string FileName, string ContentType);

/// <summary>
/// Сообщение для отправки в WhatsApp через шлюз. Идёт через durable-очередь: переживает перезапуск сервиса,
/// а если номер временно не подключён или шлюз недоступен — отправка повторяется.
/// Текст (если есть) уходит первым, затем файлы по порядку. Если текста нет, подпись получает первый файл.
/// </summary>
public sealed record SendWhatsAppMessage(Guid ChannelId, string ChatJid, string Text, IReadOnlyList<WhatsAppFile>? Files = null,
    string? FirstCaption = null);

/// <summary>Шлюз недоступен или номер не подключён: Wolverine повторит отправку.</summary>
public sealed class WhatsAppRetryException(string message) : Exception(message);

/// <summary>Исходящие сообщения в WhatsApp.</summary>
public sealed class WhatsAppOutbox(IMessageBus bus)
{
    public const string QueueName = "whatsapp-outgoing";

    /// <summary>WhatsApp принимает очень длинные сообщения, но читать их неудобно: делим по 4000 символов.</summary>
    private const int ChunkLength = 4000;

    public async Task SendTextAsync(Guid channelId, string chatJid, string text) =>
        await bus.PublishAsync(new SendWhatsAppMessage(channelId, chatJid, text));

    /// <summary>Ответ оператора: псевдоним жирным (*…*), затем текст и файлы. ФИО и логин в WhatsApp не уходят.</summary>
    public async Task SendOperatorReplyAsync(Guid channelId, string chatJid, string alias, string text,
        IReadOnlyList<StoredFile>? files = null)
    {
        var whatsAppFiles = files?.Select(f => new WhatsAppFile(f.StorageKey, f.FileName, f.ContentType)).ToList() ?? [];
        var chunks = Chunk(text).ToList();
        var signature = $"*{alias.Replace("*", string.Empty)}*";
        if (chunks.Count == 0)
        {
            if (whatsAppFiles.Count > 0)
                await bus.PublishAsync(new SendWhatsAppMessage(channelId, chatJid, string.Empty, whatsAppFiles, signature));
            return;
        }
        for (var i = 0; i < chunks.Count; i++)
        {
            var body = i == 0 ? $"{signature}\n{chunks[i]}" : chunks[i];
            var last = i == chunks.Count - 1;
            await bus.PublishAsync(new SendWhatsAppMessage(channelId, chatJid, body, last && whatsAppFiles.Count > 0 ? whatsAppFiles : null));
        }
    }

    private static IEnumerable<string> Chunk(string text)
    {
        for (var i = 0; i < text.Length; i += ChunkLength)
            yield return text.Substring(i, Math.Min(ChunkLength, text.Length - i));
    }
}

/// <summary>Обработчик очереди исходящих сообщений WhatsApp (Wolverine).</summary>
public sealed class SendWhatsAppMessageHandler(
    RelayoDbContext db,
    WhatsAppGateway gateway,
    IFileStorage storage,
    ILogger<SendWhatsAppMessageHandler> logger)
{
    public async Task Handle(SendWhatsAppMessage message, CancellationToken ct)
    {
        var channel = await db.WhatsAppChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == message.ChannelId, ct);
        if (channel is not { IsEnabled: true, Mode: WhatsAppMode.Gateway })
        {
            logger.LogWarning("WhatsApp channel {ChannelId} is disabled, removed or link-only: a message is dropped", message.ChannelId);
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(message.Text))
                await gateway.SendTextAsync(message.ChannelId, message.ChatJid, message.Text, ct);

            var caption = message.FirstCaption;
            foreach (var file in message.Files ?? [])
            {
                var content = await storage.ReadAttachmentAsync(file.StorageKey, ct);
                await gateway.SendFileAsync(message.ChannelId, message.ChatJid, content, file.FileName, file.ContentType, caption, ct);
                caption = null;
            }
        }
        catch (WhatsAppNotConnectedException)
        {
            throw new WhatsAppRetryException($"WhatsApp channel {message.ChannelId} is not connected");
        }
        catch (DomainException ex) when (ex.Code == ErrorCodes.WhatsAppGatewayUnavailable)
        {
            throw new WhatsAppRetryException(ex.Message);
        }
    }
}
