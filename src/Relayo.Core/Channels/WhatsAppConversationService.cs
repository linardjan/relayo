using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Clients;
using Relayo.Core.Conversations;
using Relayo.Core.Data;
using Relayo.Core.Files;
using Relayo.Core.Sites;

namespace Relayo.Core.Channels;

/// <summary>Сообщение клиента из WhatsApp в том виде, в каком его передаёт шлюз.</summary>
/// <param name="ChatJid">Куда отвечать: «номер@s.whatsapp.net» или скрытый id «…@lid».</param>
/// <param name="Unsupported">Что клиент прислал, если это не текст и не файл (геопозиция, контакт…), или пометка о файле.</param>
public sealed record WhatsAppIncoming(
    string MessageId,
    string ChatJid,
    string? Phone,
    string? PushName,
    string Text,
    IReadOnlyList<FileUpload> Files,
    string? Unsupported);

public enum WhatsAppLinkResult
{
    /// <summary>Кода в сообщении нет: обычное сообщение.</summary>
    None,

    /// <summary>Номер привязан к посетителю виджета, переписка продолжается в WhatsApp.</summary>
    Linked,

    /// <summary>Этот код уже привязал этот номер раньше (сообщение повторилось).</summary>
    AlreadyLinked,

    /// <summary>Код просрочен, уже использован или от другого номера.</summary>
    Expired,
}

/// <summary>Что получилось: сообщения для операторов и виджета, и что ответить клиенту про код.</summary>
public sealed record WhatsAppReceived(IReadOnlyList<PostedVisitorMessage> Posted, WhatsAppLinkResult Link);

/// <summary>
/// Переписка через WhatsApp (режим QR): коды перехода из виджета и сообщения клиента (п. 3.3 ТЗ, шаги 6 и 7).
/// Связи со шлюзом здесь нет: этим занимается внутренний сервис.
/// </summary>
public sealed partial class WhatsAppConversationService(
    RelayoDbContext db,
    WidgetConversationService conversations,
    AttachmentService attachments,
    TimeProvider time)
{
    public const int CodeLength = 8;

    // Без похожих символов (0/O, 1/I/L): код могут прочитать вслух или перепечатать.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public Task<WhatsAppChannel?> FindChannelAsync(Guid channelId, CancellationToken ct) =>
        db.WhatsAppChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId, ct);

    /// <summary>
    /// Ссылка wa.me/&lt;номер&gt;?text=My code: XXXXXXXX. Код подставится в поле ввода WhatsApp, и по нему первое
    /// сообщение попадёт в переписку посетителя. Без подключения по QR — просто ссылка на номер.
    /// </summary>
    public async Task<MessengerLink> CreateLinkAsync(Widget widget, string visitorToken, CancellationToken ct)
    {
        var channel = widget.WhatsAppChannelId is { } channelId
            ? await db.WhatsAppChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == channelId, ct)
            : null;
        if (channel is not { CanRedirect: true })
            throw new DomainException(ErrorCodes.WhatsAppNotConnected, "WhatsApp is not connected to this widget");
        var now = time.GetUtcNow();
        var plain = $"https://wa.me/{channel.PhoneNumber}";
        if (channel.Mode != WhatsAppMode.Gateway)
            return new MessengerLink(plain, now.AddDays(1));

        var visitor = await conversations.FindVisitorAsync(widget, visitorToken, ct)
                      ?? throw new DomainException(ErrorCodes.VisitorNotFound, "Visitor not found");

        await db.WhatsAppLinkCodes.Where(c => c.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);
        var code = NewCode();
        var link = new WhatsAppLinkCode(VisitorTokens.Hash(code), channel.Id, visitor.Id, now);
        db.WhatsAppLinkCodes.Add(link);
        await db.SaveChangesAsync(ct);
        return new MessengerLink($"{plain}?text={Uri.EscapeDataString(CodeText(code))}", link.ExpiresAt);
    }

    /// <summary>Текст, который виджет подставляет в поле ввода WhatsApp.</summary>
    public static string CodeText(string code) => $"My code: {code}";

    /// <summary>
    /// Сообщение клиента на номер компании. Если раньше человек на этот номер не писал, заводится новый
    /// анонимный клиент (п. 3.3, шаг 7). Если в сообщении код из виджета — номер привязывается к посетителю.
    /// Файлы кладутся в S3 до транзакции: неподходящий файл не теряет сообщение, оператор видит пометку.
    /// </summary>
    public async Task<WhatsAppReceived> ReceiveAsync(WhatsAppChannel channel, WhatsAppIncoming incoming, CancellationToken ct)
    {
        var (code, text) = ExtractCode(incoming.Text);
        var notes = new List<string>();
        if (incoming.Unsupported is { Length: > 0 } kind)
            notes.Add($"[The client sent a {kind}: it is not supported]");

        IReadOnlyList<StoredFile> files = [];
        if (incoming.Files.Count > 0 && !await IsDuplicateAsync(channel, incoming, ct))
        {
            try
            {
                files = await attachments.StoreAsync(incoming.Files, ct);
            }
            catch (DomainException ex)
            {
                notes.Add($"[The client sent {string.Join(", ", incoming.Files.Select(f => f.FileName))}: {ex.Message}]");
            }
        }
        text = string.Join('\n', new[] { text }.Concat(notes).Where(t => t.Length > 0));
        if (text.Length > Message.TextMaxLength)
            text = text[..Message.TextMaxLength];

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // С кодом из виджета клиента не заводим заранее: скорее всего, им станет клиент посетителя.
        var identity = await GetOrCreateIdentityAsync(channel, incoming, createClient: code is null, now, ct);

        var posted = new List<PostedVisitorMessage>();
        var link = WhatsAppLinkResult.None;
        if (code is not null)
        {
            (link, var system) = await LinkAsync(channel, identity, code, incoming, now, ct);
            if (system is not null)
                posted.Add(system);
        }

        // Сообщение только с кодом — это переход из виджета, а не вопрос: в чат его не пишем.
        if (text.Length > 0 || files.Count > 0)
        {
            var message = await conversations.PostAsync(identity, Channel.WhatsApp, text, $"wa:{incoming.MessageId}", now, ct, files);
            identity.Client?.SetDisplayNameIfEmpty(DisplayName(incoming));
            await db.SaveChangesAsync(ct);
            posted.Add(message);
        }

        await tx.CommitAsync(ct);
        return new WhatsAppReceived(posted, link);
    }

    /// <summary>«My code: AB12CD34» в любом месте сообщения → код и текст без этой строки.</summary>
    internal static (string? Code, string Text) ExtractCode(string? text)
    {
        text = text?.Trim() ?? string.Empty;
        var match = CodePattern().Match(text);
        if (!match.Success)
            return (null, text);
        var rest = (text[..match.Index] + text[(match.Index + match.Length)..]).Trim();
        return (match.Groups[1].Value.ToUpperInvariant(), rest);
    }

    private async Task<(WhatsAppLinkResult, PostedVisitorMessage?)> LinkAsync(WhatsAppChannel channel, ClientIdentity identity,
        string code, WhatsAppIncoming incoming, DateTimeOffset now, CancellationToken ct)
    {
        var hash = VisitorTokens.Hash(code);
        var link = await db.WhatsAppLinkCodes.SingleOrDefaultAsync(c => c.CodeHash == hash && c.ChannelId == channel.Id, ct);
        var visitor = link is null
            ? null
            : await db.ClientIdentities.Include(i => i.Client)
                .SingleOrDefaultAsync(i => i.Id == link.VisitorIdentityId && i.Type == IdentityType.Visitor, ct);
        if (link is null || visitor is null || link.IsExpired(now))
            return (WhatsAppLinkResult.Expired, null);
        if (link.UsedAt is not null)
            // Тот же код ещё раз (WhatsApp повторил сообщение или человек отправил его дважды) — не ошибка.
            return (identity.LinkedFromIdentityId == visitor.Id ? WhatsAppLinkResult.AlreadyLinked : WhatsAppLinkResult.Expired, null);
        link.MarkUsed(now);

        if (visitor.Client is null)
        {
            var anonymous = Client.CreateAnonymous(visitor.SiteId, now);
            db.Clients.Add(anonymous);
            visitor.AttachTo(anonymous, verified: false);
            await db.SaveChangesAsync(ct);
        }
        var target = visitor.Client!;
        await conversations.LockClientAsync(target.Id, ct);

        if (identity.Client is { } previous && previous.Id != target.Id && previous.IsAnonymous)
        {
            // Человек уже писал на этот номер: его отдельный анонимный клиент объединяется с клиентом посетителя.
            await conversations.LockClientAsync(previous.Id, ct);
            await conversations.MergeAnonymousAsync(previous, target, identity.Id, now, ct);
        }
        identity.LinkFrom(visitor, target);
        identity.Touch(now);
        target.SetDisplayNameIfEmpty(DisplayName(incoming));
        await db.SaveChangesAsync(ct);

        // Оператор и посетитель видят в чате, что переписка продолжилась в WhatsApp.
        PostedVisitorMessage? system = null;
        if (await FindVisitorChatAsync(target.Id, visitor.Id, ct) is { } chat)
        {
            var message = Message.System(chat, identity.Id, Channel.WhatsApp, "The client continued the chat in WhatsApp", now);
            db.Messages.Add(message);
            chat.OnMessage(now);
            await db.SaveChangesAsync(ct);
            system = new PostedVisitorMessage(WidgetConversationService.ToView(message, null), chat.TicketId, false,
                await conversations.GetRecipientsAsync(chat, ct));
        }
        return (WhatsAppLinkResult.Linked, system);
    }

    private async Task<bool> IsDuplicateAsync(WhatsAppChannel channel, WhatsAppIncoming incoming, CancellationToken ct)
    {
        var externalId = ClientIdentity.WhatsAppExternalId(channel.Id, incoming.ChatJid);
        var clientMessageId = $"wa:{incoming.MessageId}";
        var since = time.GetUtcNow().AddDays(-1);
        return await (
            from m in db.Messages
            join i in db.ClientIdentities on m.VisitorIdentityId equals i.Id
            where i.Type == IdentityType.WhatsApp && i.ExternalId == externalId && m.ClientMessageId == clientMessageId
                  && m.CreatedAt >= since
            select m.Id).AnyAsync(ct);
    }

    private Task<ClientIdentity?> FindIdentityAsync(Guid channelId, string chatJid, CancellationToken ct)
    {
        var externalId = ClientIdentity.WhatsAppExternalId(channelId, chatJid);
        return db.ClientIdentities.Include(i => i.Client)
            .SingleOrDefaultAsync(i => i.Type == IdentityType.WhatsApp && i.ExternalId == externalId, ct);
    }

    private async Task<ClientIdentity> GetOrCreateIdentityAsync(WhatsAppChannel channel, WhatsAppIncoming incoming,
        bool createClient, DateTimeOffset now, CancellationToken ct)
    {
        var identity = await FindIdentityAsync(channel.Id, incoming.ChatJid, ct);
        if (identity is null)
        {
            // Два первых сообщения подряд не должны создать двух клиентов.
            await db.LockAsync("wa:" + channel.Id + ":" + incoming.ChatJid, ct);
            identity = await FindIdentityAsync(channel.Id, incoming.ChatJid, ct);
        }
        if (identity is null)
        {
            identity = ClientIdentity.CreateWhatsApp(channel.Id, incoming.ChatJid, channel.SiteId, now);
            db.ClientIdentities.Add(identity);
        }
        if (identity.Client is null && createClient)
        {
            var client = Client.CreateAnonymous(identity.SiteId, now);
            client.SetDisplayNameIfEmpty(DisplayName(incoming));
            db.Clients.Add(client);
            identity.AttachTo(client, verified: false);
        }
        identity.SetDisplayName(DisplayName(incoming));
        await db.SaveChangesAsync(ct);
        return identity;
    }

    /// <summary>Имя из профиля и номер: «Иван (+79001234567)». Номер может быть скрыт — тогда только имя.</summary>
    internal static string DisplayName(WhatsAppIncoming incoming)
    {
        var name = incoming.PushName?.Trim() ?? string.Empty;
        var phone = incoming.Phone is { Length: > 0 } p ? $"+{p}" : null;
        return (name, phone) switch
        {
            ({ Length: > 0 }, not null) => $"{name} ({phone})",
            ({ Length: > 0 }, null) => name,
            (_, not null) => phone,
            _ => "WhatsApp",
        };
    }

    /// <summary>Последний чат открытого тикета, в котором писал посетитель.</summary>
    private async Task<Chat?> FindVisitorChatAsync(Guid clientId, Guid visitorId, CancellationToken ct)
    {
        var ticket = await conversations.FindOpenTicketAsync(clientId, ct);
        if (ticket is null)
            return null;
        var chatIds = ticket.Chats.Select(c => c.Id).ToList();
        var chatId = await db.Messages
            .Where(m => chatIds.Contains(m.ChatId) && m.VisitorIdentityId == visitorId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (Guid?)m.ChatId)
            .FirstOrDefaultAsync(ct);
        return chatId is { } id ? ticket.Chats.Single(c => c.Id == id) : null;
    }

    private static string NewCode() =>
        string.Create(CodeLength, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        });

    [GeneratedRegex(@"\bmy code:\s*([A-Za-z0-9]{8})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodePattern();
}
