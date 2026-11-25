using Microsoft.EntityFrameworkCore;
using Relayo.Core.Clients;
using Relayo.Core.Data;
using Relayo.Core.Sites;

namespace Relayo.Core.Conversations;

public sealed record VisitorSession(string Token, ClientIdentity Visitor);

public sealed record MessageView(
    Guid Id,
    Guid ChatId,
    MessageAuthor Author,
    string? AuthorName,
    string Text,
    DateTimeOffset CreatedAt,
    string? ClientMessageId);

/// <summary>
/// Кому из посетителей виджета доставить сообщение в реальном времени:
/// посетителям, писавшим в этом чате, и, если чат подтверждённый, всем подтверждённым сессиям клиента.
/// </summary>
public sealed record WidgetRecipients(IReadOnlyList<Guid> VisitorIds, Guid? VerifiedClientId);

public sealed record PostedVisitorMessage(MessageView Message, Guid TicketId, bool TicketCreated, WidgetRecipients Recipients,
    bool Duplicate = false);

/// <summary>Переписка со стороны посетителя виджета.</summary>
public sealed class WidgetConversationService(RelayoDbContext db, ClientTokenValidator clientTokens, TimeProvider time)
{
    /// <summary>За сколько дней посетитель видит историю (п. 3.5 ТЗ).</summary>
    public const int HistoryDays = 30;

    private const int HistoryLimit = 500;
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    public Task<Widget?> FindWidgetAsync(Guid widgetId, CancellationToken ct) =>
        db.Widgets.AsNoTracking().Include(w => w.Site).SingleOrDefaultAsync(w => w.Id == widgetId, ct);

    public async Task<Widget> GetWidgetAsync(Guid widgetId, CancellationToken ct) =>
        await FindWidgetAsync(widgetId, ct)
        ?? throw new DomainException(ErrorCodes.WidgetNotFound, "Widget not found");

    /// <summary>Домены всех сайтов: для CORS публичного сервиса.</summary>
    public async Task<IReadOnlyList<string>> GetAllowedOriginsAsync(CancellationToken ct) =>
        (await db.Sites.AsNoTracking().Select(s => s.AllowedOrigins).ToListAsync(ct))
        .SelectMany(o => o).Distinct().ToList();

    /// <summary>Находит посетителя по токену или, если разрешено, создаёт нового.</summary>
    public async Task<VisitorSession> StartVisitorAsync(Widget widget, string? token, bool createIfMissing,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!string.IsNullOrEmpty(token) && await FindVisitorAsync(widget, token, ct) is { } existing)
        {
            if (now - existing.LastSeenAt > TouchInterval)
            {
                existing.Touch(now);
                await db.SaveChangesAsync(ct);
            }
            return new VisitorSession(token, existing);
        }

        if (!createIfMissing)
            throw new DomainException(ErrorCodes.VisitorNotFound, "Visitor not found");

        var raw = VisitorTokens.Create();
        var visitor = ClientIdentity.CreateVisitor(widget.SiteId, widget.Id, VisitorTokens.Hash(raw), now);
        db.ClientIdentities.Add(visitor);
        await db.SaveChangesAsync(ct);
        return new VisitorSession(raw, visitor);
    }

    /// <summary>Relayo.identify(token): сайт подтвердил клиента подписанным токеном.</summary>
    public async Task<VisitorSession> IdentifyWithTokenAsync(Widget widget, string visitorToken, string clientToken,
        CancellationToken ct)
    {
        if (widget.IdentificationMode != IdentificationMode.SignedToken)
            throw new DomainException(ErrorCodes.WrongMode, "This widget does not accept signed tokens");

        var claims = await clientTokens.ValidateAsync(clientToken, ct)
                     ?? throw new DomainException(ErrorCodes.InvalidToken, "Client token is invalid or expired");
        if (claims.SiteId != widget.SiteId)
            throw new DomainException(ErrorCodes.SiteMismatch, "Client token was signed by another site");

        return await LinkAsync(widget, visitorToken, widget.SiteId, claims.ClientId, claims.Locale, verified: true, ct);
    }

    /// <summary>Relayo.setClientId() / setProduct(): небезопасный режим, clientId не подтверждён (п. 3.3.2 ТЗ).</summary>
    public async Task<VisitorSession> IdentifyDirectAsync(Widget widget, string visitorToken, string clientId,
        string? product, string? locale, CancellationToken ct)
    {
        if (widget.IdentificationMode != IdentificationMode.Direct)
            throw new DomainException(ErrorCodes.WrongMode, "This widget accepts only signed tokens");

        clientId = clientId.Trim();
        if (clientId.Length is 0 or > Client.ExternalIdMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, "clientId is empty or too long");

        var siteId = widget.SiteId;
        if (!string.IsNullOrWhiteSpace(product) && product.Trim() != widget.Site.Code)
        {
            siteId = await db.Sites.Where(s => s.Code == product.Trim()).Select(s => (Guid?)s.Id)
                         .SingleOrDefaultAsync(ct)
                     ?? throw new DomainException(ErrorCodes.UnknownProduct, "Unknown product");
        }

        return await LinkAsync(widget, visitorToken, siteId, clientId, locale, verified: false, ct);
    }

    public async Task<PostedVisitorMessage> SendAsync(Widget widget, string visitorToken, string text,
        string? clientMessageId, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length is 0 or > Message.TextMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, "Message is empty or too long");
        if (clientMessageId is { Length: > Message.ClientMessageIdMaxLength })
            throw new DomainException(ErrorCodes.InvalidInput, "clientMessageId is too long");

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var visitor = await FindVisitorAsync(widget, visitorToken, ct)
                      ?? throw new DomainException(ErrorCodes.VisitorNotFound, "Visitor not found");
        var posted = await PostAsync(visitor, Channel.Widget, text, clientMessageId, now, ct);
        await tx.CommitAsync(ct);
        return posted;
    }

    /// <summary>
    /// Сообщение клиента из любого канала: находит или открывает тикет и выбирает чат (п. 3.2 ТЗ).
    /// Вызывается внутри открытой транзакции; у идентичности должен быть загружен Client.
    /// </summary>
    internal async Task<PostedVisitorMessage> PostAsync(ClientIdentity identity, Channel channel, string text,
        string? clientMessageId, DateTimeOffset now, CancellationToken ct)
    {
        if (identity.Client is null)
        {
            var anonymous = Client.CreateAnonymous(identity.SiteId, now);
            db.Clients.Add(anonymous);
            identity.AttachTo(anonymous, verified: false);
            await db.SaveChangesAsync(ct);
        }
        var clientId = identity.ClientId!.Value;
        await LockClientAsync(clientId, ct);

        // Повторная отправка того же сообщения (например, после обрыва связи) не создаёт дубль.
        if (!string.IsNullOrEmpty(clientMessageId))
        {
            var duplicate = await db.Messages.AsNoTracking()
                .Where(m => m.VisitorIdentityId == identity.Id && m.ClientMessageId == clientMessageId
                            && m.CreatedAt >= now.AddDays(-1))
                .FirstOrDefaultAsync(ct);
            if (duplicate is not null)
            {
                var dupChat = await db.Chats.AsNoTracking().SingleAsync(c => c.Id == duplicate.ChatId, ct);
                return new PostedVisitorMessage(ToView(duplicate, null), dupChat.TicketId, false,
                    await GetRecipientsAsync(dupChat, ct), Duplicate: true);
            }
        }

        var ticket = await FindOpenTicketAsync(clientId, ct);

        var ticketCreated = false;
        Chat chat;
        if (ticket is null)
        {
            ticket = Ticket.Open(clientId, now);
            db.Tickets.Add(ticket);
            chat = Chat.Start(ticket, identity.IsVerified, now);
            ticketCreated = true;
        }
        else
        {
            chat = await ChooseChatAsync(ticket, identity, now, ct);
        }

        var message = Message.FromClient(chat, identity.Id, channel, text, clientMessageId, now);
        db.Messages.Add(message);
        chat.OnMessage(now);
        ticket.OnClientMessage(text, channel, now);
        identity.Touch(now);

        await db.SaveChangesAsync(ct);

        return new PostedVisitorMessage(ToView(message, null), ticket.Id, ticketCreated,
            await GetRecipientsAsync(chat, ct));
    }

    internal Task<Ticket?> FindOpenTicketAsync(Guid clientId, CancellationToken ct) =>
        db.Tickets.Include(t => t.Chats)
            .Where(t => t.ClientId == clientId && t.Status != TicketStatus.Closed)
            .OrderByDescending(t => t.LastMessageAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// История для виджета за <see cref="HistoryDays"/> дней. Подтверждённый посетитель видит
    /// подтверждённые чаты клиента, неподтверждённый — только чаты, где писал сам.
    /// </summary>
    public async Task<IReadOnlyList<MessageView>> GetHistoryAsync(Widget widget, string visitorToken, CancellationToken ct)
    {
        var visitor = await FindVisitorAsync(widget, visitorToken, ct)
                      ?? throw new DomainException(ErrorCodes.VisitorNotFound, "Visitor not found");
        if (visitor.ClientId is not { } clientId)
            return [];

        var since = time.GetUtcNow().AddDays(-HistoryDays);
        var ownChats = db.Messages
            .Where(m => m.VisitorIdentityId == visitor.Id && m.CreatedAt >= since)
            .Select(m => m.ChatId);
        var chats = visitor.IsVerified
            ? db.Chats.Where(c => c.ClientId == clientId && (c.IsVerified || ownChats.Contains(c.Id)))
            : db.Chats.Where(c => ownChats.Contains(c.Id));
        var chatIds = await chats.Where(c => c.LastMessageAt >= since).Select(c => c.Id).ToListAsync(ct);
        if (chatIds.Count == 0)
            return [];

        var rows = await (
                from m in db.Messages.AsNoTracking()
                where chatIds.Contains(m.ChatId) && m.CreatedAt >= since
                join u in db.Users on m.OperatorId equals u.Id into operators
                from u in operators.DefaultIfEmpty()
                orderby m.CreatedAt descending
                select new { Message = m, Alias = u == null ? null : u.Alias })
            .Take(HistoryLimit)
            .ToListAsync(ct);

        return rows.AsEnumerable().Reverse().Select(r => ToView(r.Message, r.Alias)).ToList();
    }

    public async Task<WidgetRecipients> GetRecipientsAsync(Chat chat, CancellationToken ct)
    {
        var visitors = await db.Messages
            .Where(m => m.ChatId == chat.Id && m.VisitorIdentityId != null)
            .Select(m => m.VisitorIdentityId!.Value)
            .Distinct()
            .ToListAsync(ct);
        return new WidgetRecipients(visitors, chat.IsVerified ? chat.ClientId : null);
    }

    internal static MessageView ToView(Message message, string? operatorAlias) => new(
        message.Id, message.ChatId, message.Author,
        message.Author == MessageAuthor.Operator ? operatorAlias : null,
        message.Text, message.CreatedAt, message.ClientMessageId);

    internal Task<ClientIdentity?> FindVisitorAsync(Widget widget, string token, CancellationToken ct)
    {
        var hash = VisitorTokens.Hash(token);
        return db.ClientIdentities.Include(i => i.Client)
            .SingleOrDefaultAsync(i => i.Type == IdentityType.Visitor && i.ExternalId == hash
                                                                     && i.SiteId == widget.SiteId, ct);
    }

    /// <summary>Выбирает чат открытого тикета, в который пойдёт сообщение посетителя.</summary>
    private async Task<Chat> ChooseChatAsync(Ticket ticket, ClientIdentity visitor, DateTimeOffset now,
        CancellationToken ct)
    {
        var chatIds = ticket.Chats.Select(c => c.Id).ToList();
        // Клиент, перешедший из виджета в мессенджер, продолжает чат посетителя, из которого пришёл.
        var identityIds = new List<Guid> { visitor.Id };
        if (visitor.LinkedFromIdentityId is { } linkedFrom)
            identityIds.Add(linkedFrom);
        var ownChatId = await db.Messages
            .Where(m => chatIds.Contains(m.ChatId) && m.VisitorIdentityId != null
                        && identityIds.Contains(m.VisitorIdentityId.Value))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (Guid?)m.ChatId)
            .FirstOrDefaultAsync(ct);
        if (ownChatId is { } id)
            return ticket.Chats.Single(c => c.Id == id);

        // Неподтверждённый посетитель не попадает в чужую переписку: у него свой чат в этом тикете.
        if (!visitor.IsVerified)
            return StartChat(ticket, verified: false, now);

        return ticket.Chats.Where(c => c.IsVerified).MaxBy(c => c.LastMessageAt)
               ?? StartChat(ticket, verified: true, now);
    }

    /// <summary>
    /// Новый чат в уже существующем тикете нужно явно добавить в контекст: ключ задан заранее,
    /// и через навигацию EF принял бы его за существующую запись.
    /// </summary>
    internal Chat StartChat(Ticket ticket, bool verified, DateTimeOffset now)
    {
        var chat = Chat.Start(ticket, verified, now);
        db.Chats.Add(chat);
        return chat;
    }

    private async Task<VisitorSession> LinkAsync(Widget widget, string visitorToken, Guid siteId,
        string externalClientId, string? locale, bool verified, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var visitor = await FindVisitorAsync(widget, visitorToken, ct)
                      ?? throw new DomainException(ErrorCodes.VisitorNotFound, "Visitor not found");

        var target = await GetOrCreateClientAsync(siteId, externalClientId, now, ct);
        await LockClientAsync(target.Id, ct);
        target.SetLocale(locale);
        if (verified)
            target.MarkVerified();

        var token = visitorToken;
        var current = visitor.Client;
        if (current?.Id == target.Id)
        {
            if (verified && !visitor.IsVerified)
            {
                visitor.MarkVerified();
                await MarkOwnChatsVerifiedAsync(visitor.Id, ct);
            }
        }
        else if (current is { IsAnonymous: false })
        {
            // Браузер уже привязан к другому клиенту (сменили пользователя без logout).
            // Его переписку новому клиенту не показываем: заводим нового посетителя.
            token = VisitorTokens.Create();
            var fresh = ClientIdentity.CreateVisitor(widget.SiteId, widget.Id, VisitorTokens.Hash(token), now);
            fresh.AttachTo(target, verified);
            db.ClientIdentities.Add(fresh);
            visitor = fresh;
        }
        else
        {
            visitor.AttachTo(target, verified);
            if (current is not null)
                await MergeAnonymousAsync(current, target, visitor.Id, now, ct);
            await db.SaveChangesAsync(ct);
            if (verified)
                await MarkOwnChatsVerifiedAsync(visitor.Id, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new VisitorSession(token, visitor);
    }

    private async Task<Client> GetOrCreateClientAsync(Guid siteId, string externalClientId, DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = await db.Clients
            .SingleOrDefaultAsync(c => c.SiteId == siteId && c.ExternalClientId == externalClientId, ct);
        if (existing is not null)
            return existing;

        // Блокировка по (сайт, clientId): два одновременных запроса не создадут двух клиентов.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({siteId + ":" + externalClientId}, 0))", ct);
        existing = await db.Clients
            .SingleOrDefaultAsync(c => c.SiteId == siteId && c.ExternalClientId == externalClientId, ct);
        if (existing is not null)
            return existing;

        var created = Client.CreateIdentified(siteId, externalClientId, now);
        db.Clients.Add(created);
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>Переносит переписку анонимного клиента к опознанному и удаляет анонимного.</summary>
    internal async Task MergeAnonymousAsync(Client source, Client target, Guid visitorId, DateTimeOffset now,
        CancellationToken ct)
    {
        var targetOpen = await db.Tickets.Include(t => t.Chats)
            .Where(t => t.ClientId == target.Id && t.Status != TicketStatus.Closed)
            .OrderByDescending(t => t.LastMessageAt)
            .FirstOrDefaultAsync(ct);

        var sourceTickets = await db.Tickets.Include(t => t.Chats)
            .Where(t => t.ClientId == source.Id)
            .ToListAsync(ct);

        foreach (var ticket in sourceTickets)
        {
            if (ticket.IsOpen && targetOpen is not null)
            {
                // У клиента уже есть открытый тикет: чаты анонимного тикета переходят в него.
                foreach (var chat in ticket.Chats.ToList())
                {
                    ticket.Chats.Remove(chat);
                    targetOpen.Chats.Add(chat);
                    chat.MoveTo(targetOpen);
                }
                ticket.MoveToClient(target.Id);
                ticket.CloseAsMergedInto(targetOpen.Id, now);
                targetOpen.OnMergedFrom(ticket, now);
            }
            else
            {
                ticket.MoveToClient(target.Id);
                foreach (var chat in ticket.Chats)
                    chat.MoveToClient(target.Id);
                if (ticket.IsOpen)
                    targetOpen = ticket;
            }
        }

        var otherIdentities = await db.ClientIdentities
            .Where(i => i.ClientId == source.Id && i.Id != visitorId)
            .ToListAsync(ct);
        foreach (var identity in otherIdentities)
            identity.AttachTo(target, identity.IsVerified);

        await db.SaveChangesAsync(ct);
        db.Clients.Remove(source);
    }

    /// <summary>
    /// Посетитель подтвердил личность: подтверждаются его чаты и мессенджеры, в которые он перешёл из виджета.
    /// </summary>
    private async Task MarkOwnChatsVerifiedAsync(Guid visitorId, CancellationToken ct)
    {
        await db.ClientIdentities
            .Where(i => i.LinkedFromIdentityId == visitorId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.IsVerified, true), ct);
        await db.Chats
            .Where(c => db.Messages.Any(m => m.ChatId == c.Id && m.VisitorIdentityId != null
                && (m.VisitorIdentityId == visitorId
                    || db.ClientIdentities.Any(i => i.Id == m.VisitorIdentityId && i.LinkedFromIdentityId == visitorId))))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsVerified, true), ct);
    }

    /// <summary>Сообщения одного клиента обрабатываются по очереди: иначе два первых сообщения создали бы два тикета.</summary>
    internal Task LockClientAsync(Guid clientId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({clientId.ToString()}, 0))", ct);
}
