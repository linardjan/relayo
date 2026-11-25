using Microsoft.EntityFrameworkCore;
using Relayo.Core.Clients;
using Relayo.Core.Data;
using Relayo.Core.Users;

namespace Relayo.Core.Conversations;

public sealed record TicketSummary(
    Guid Id,
    long Number,
    TicketStatus Status,
    Guid ClientId,
    string ClientLabel,
    bool ClientVerified,
    string SiteCode,
    Guid? AssigneeId,
    string? AssigneeName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastMessageAt,
    string LastMessagePreview,
    Channel LastClientChannel);

public sealed record ClientCard(Guid Id, string Label, string SiteCode, string? ExternalClientId, string? Locale,
    bool IsVerified);

/// <summary>Сообщение в админке: автор-оператор показывается по ФИО, его видят только сотрудники.</summary>
public sealed record OperatorMessageView(
    Guid Id,
    Guid ChatId,
    MessageAuthor Author,
    string? AuthorName,
    string Text,
    DateTimeOffset CreatedAt,
    Channel Channel,
    bool Automated = false);

/// <param name="ReplyChannel">Куда уйдёт ответ оператора: в канал, из которого клиент написал последним (п. 3.2 ТЗ).</param>
public sealed record ChatView(Guid Id, bool IsVerified, DateTimeOffset CreatedAt, Channel ReplyChannel,
    IReadOnlyList<OperatorMessageView> Messages);

public sealed record TicketDetails(TicketSummary Ticket, ClientCard Client, IReadOnlyList<ChatView> Chats);

/// <summary>Ответ нужно доставить в Telegram: через этого бота в этот чат. Без псевдонима — автоответ.</summary>
public sealed record TelegramDelivery(Guid BotId, long ChatId, string? OperatorAlias, string Text);

public sealed record OperatorReply(
    OperatorMessageView Message,
    MessageView WidgetMessage,
    TicketSummary Ticket,
    WidgetRecipients Recipients,
    TelegramDelivery? Telegram);

/// <summary>Работа операторов с тикетами.</summary>
public sealed class TicketService(RelayoDbContext db, WidgetConversationService widgets, TimeProvider time)
{
    /// <summary>Подпись автоответов правил в админке.</summary>
    public const string AutoReplyName = "Auto-reply";

    private const int ListLimit = 200;
    private const int MessagesPerTicketLimit = 1000;

    public async Task<IReadOnlyList<TicketSummary>> ListAsync(bool open, CancellationToken ct)
    {
        var query = Summaries().Where(s => open
            ? s.Ticket.Status != TicketStatus.Closed
            : s.Ticket.Status == TicketStatus.Closed && s.Ticket.MergedIntoId == null);
        var rows = await query.OrderByDescending(s => s.Ticket.LastMessageAt).Take(ListLimit).ToListAsync(ct);
        return rows.Select(ToSummary).ToList();
    }

    public async Task<TicketSummary> GetSummaryAsync(Guid ticketId, CancellationToken ct)
    {
        var row = await Summaries().SingleOrDefaultAsync(s => s.Ticket.Id == ticketId, ct)
                  ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");
        return ToSummary(row);
    }

    public async Task<TicketDetails> GetAsync(Guid ticketId, CancellationToken ct)
    {
        var row = await Summaries().SingleOrDefaultAsync(s => s.Ticket.Id == ticketId, ct)
                  ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");

        var chats = await db.Chats.AsNoTracking().Where(c => c.TicketId == ticketId)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var chatIds = chats.Select(c => c.Id).ToList();
        var messages = await (
                from m in db.Messages.AsNoTracking()
                where chatIds.Contains(m.ChatId)
                join u in db.Users on m.OperatorId equals u.Id into operators
                from u in operators.DefaultIfEmpty()
                orderby m.CreatedAt
                select new OperatorMessageView(m.Id, m.ChatId, m.Author,
                    u == null ? (m.Author == MessageAuthor.Operator ? AutoReplyName : null) : u.FullName, m.Text,
                    m.CreatedAt, m.Channel, m.Author == MessageAuthor.Operator && m.OperatorId == null))
            .Take(MessagesPerTicketLimit)
            .ToListAsync(ct);

        var byChat = messages.ToLookup(m => m.ChatId);
        var summary = ToSummary(row);
        return new TicketDetails(
            summary,
            new ClientCard(row.ClientId, summary.ClientLabel, row.SiteCode, row.ExternalClientId, row.Locale,
                row.ClientVerified),
            chats.Select(c => new ChatView(c.Id, c.IsVerified, c.CreatedAt,
                byChat[c.Id].LastOrDefault(m => m.Author == MessageAuthor.Client)?.Channel ?? Channel.Widget,
                byChat[c.Id].ToList())).ToList());
    }

    public async Task<OperatorReply> ReplyAsync(Guid ticketId, Guid chatId, Guid operatorId, string text,
        CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length is 0 or > Message.TextMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, "Message is empty or too long");

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == operatorId, ct);
        if (string.IsNullOrWhiteSpace(user.Alias))
            throw new DomainException(ErrorCodes.AliasRequired, "Set your alias before replying to clients");

        var ticket = await db.Tickets.Include(t => t.Chats).SingleOrDefaultAsync(t => t.Id == ticketId, ct)
                     ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");
        if (!ticket.IsOpen)
            throw new DomainException(ErrorCodes.TicketClosed, "Ticket is closed");
        var chat = ticket.Chats.SingleOrDefault(c => c.Id == chatId)
                   ?? throw new DomainException(ErrorCodes.ChatNotFound, "Chat not found in this ticket");

        var (channel, route) = await ReplyRouteAsync(chat.Id, ct);
        var telegram = route is { } r ? new TelegramDelivery(r.BotId, r.ChatId, user.Alias!, text) : null;

        var now = time.GetUtcNow();
        var message = Message.FromOperator(chat, operatorId, text, channel, now);
        db.Messages.Add(message);
        chat.OnMessage(now);
        ticket.OnOperatorMessage(operatorId, text, now);
        await db.SaveChangesAsync(ct);

        return new OperatorReply(
            new OperatorMessageView(message.Id, chat.Id, message.Author, user.FullName, message.Text, message.CreatedAt,
                message.Channel),
            WidgetConversationService.ToView(message, user.Alias),
            await GetSummaryAsync(ticketId, ct),
            await widgets.GetRecipientsAsync(chat, ct),
            telegram);
    }

    /// <summary>
    /// Куда уходит ответ в чат: в канал, из которого клиент написал последним (п. 3.2 ТЗ).
    /// Для Telegram — ещё бот и чат, через которые отправлять.
    /// </summary>
    internal async Task<(Channel Channel, (Guid BotId, long ChatId)? Telegram)> ReplyRouteAsync(Guid chatId,
        CancellationToken ct)
    {
        var lastFromClient = await db.Messages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Author == MessageAuthor.Client)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Channel, m.VisitorIdentityId })
            .FirstOrDefaultAsync(ct);
        if (lastFromClient is { Channel: Channel.Telegram, VisitorIdentityId: { } identityId })
        {
            var identity = await db.ClientIdentities.AsNoTracking().SingleOrDefaultAsync(i => i.Id == identityId, ct);
            if (identity is not null && identity.TryGetTelegramChat(out var botId, out var telegramChatId))
                return (Channel.Telegram, (botId, telegramChatId));
        }
        return (Channel.Widget, null);
    }

    public async Task<TicketStatus?> GetStatusAsync(Guid ticketId, CancellationToken ct) =>
        await db.Tickets.Where(t => t.Id == ticketId).Select(t => (TicketStatus?)t.Status).SingleOrDefaultAsync(ct);

    public async Task<TicketSummary> AssignAsync(Guid ticketId, Guid operatorId, CancellationToken ct)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
                     ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");
        if (!ticket.IsOpen)
            throw new DomainException(ErrorCodes.TicketClosed, "Ticket is closed");
        ticket.Assign(operatorId, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await GetSummaryAsync(ticketId, ct);
    }

    public async Task<TicketSummary> CloseAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
                     ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");
        if (ticket.IsOpen)
        {
            ticket.Close(time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        return await GetSummaryAsync(ticketId, ct);
    }

    public async Task<User> SetAliasAsync(Guid userId, string alias, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.SetAlias(alias);
        await db.SaveChangesAsync(ct);
        return user;
    }

    private IQueryable<SummaryRow> Summaries() =>
        from t in db.Tickets.AsNoTracking()
        join c in db.Clients on t.ClientId equals c.Id
        join s in db.Sites on c.SiteId equals s.Id
        join u in db.Users on t.AssigneeId equals u.Id into assignees
        from u in assignees.DefaultIfEmpty()
        // Инициализатор свойств, а не конструктор: так EF может фильтровать и сортировать по полям строки.
        select new SummaryRow
        {
            Ticket = t,
            ClientId = c.Id,
            ExternalClientId = c.ExternalClientId,
            DisplayName = c.DisplayName,
            Locale = c.Locale,
            ClientVerified = c.IsVerified,
            SiteCode = s.Code,
            AssigneeName = u == null ? null : u.FullName,
        };

    private static TicketSummary ToSummary(SummaryRow r) => new(
        r.Ticket.Id, r.Ticket.Number, r.Ticket.Status, r.ClientId,
        ClientLabel(r.ClientId, r.ExternalClientId, r.DisplayName), r.ClientVerified, r.SiteCode,
        r.Ticket.AssigneeId, r.AssigneeName, r.Ticket.CreatedAt, r.Ticket.LastMessageAt, r.Ticket.LastMessagePreview,
        r.Ticket.LastClientChannel);

    internal static string ClientLabel(Guid clientId, string? externalClientId, string? displayName) =>
        externalClientId ?? displayName ?? $"Visitor {clientId.ToString("N")[^6..]}";

    private sealed class SummaryRow
    {
        public required Ticket Ticket { get; init; }

        public Guid ClientId { get; init; }

        public string? ExternalClientId { get; init; }

        public string? DisplayName { get; init; }

        public string? Locale { get; init; }

        public bool ClientVerified { get; init; }

        public required string SiteCode { get; init; }

        public string? AssigneeName { get; init; }
    }
}
