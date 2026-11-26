using Microsoft.EntityFrameworkCore;
using Relayo.Core.Clients;
using Relayo.Core.Data;
using Relayo.Core.Files;
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
    Channel LastClientChannel,
    string? Subject,
    TicketSource Source,
    string? SourceChannel,
    Guid? GroupId,
    string? GroupName);

public sealed record GroupOption(Guid Id, string Name);

/// <summary>Сотрудник, которому можно назначить запрос. ФИО видят только сотрудники.</summary>
public sealed record AgentOption(Guid Id, string FullName, string? Alias, OperatorStatus Status);

/// <summary>
/// Фильтры раздела «Запросы». <paramref name="Assignee"/>: «me», «none» или id сотрудника.
/// Даты — границы по созданию и последнему обновлению (последнему сообщению).
/// </summary>
public sealed record TicketFilter(
    IReadOnlyList<TicketStatus>? Statuses = null,
    Channel? Channel = null,
    string? Assignee = null,
    Guid? GroupId = null,
    Guid? SiteId = null,
    Guid? ClientId = null,
    string? Search = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null);

public sealed record TicketPage(IReadOnlyList<TicketSummary> Items, int Total);

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
    bool Automated = false,
    IReadOnlyList<AttachmentView>? Attachments = null,
    MessageVisibility Visibility = MessageVisibility.Public);

/// <param name="ReplyChannel">Куда уйдёт ответ оператора: в канал, из которого клиент написал последним (п. 3.2 ТЗ).</param>
/// <param name="PageUrl">Страница сайта, с которой клиент начал писать в виджет.</param>
public sealed record ChatView(Guid Id, bool IsVerified, DateTimeOffset CreatedAt, Channel ReplyChannel,
    IReadOnlyList<OperatorMessageView> Messages, string? PageUrl = null);

public sealed record TicketDetails(TicketSummary Ticket, ClientCard Client, IReadOnlyList<ChatView> Chats);

/// <summary>Ответ нужно доставить в Telegram: через этого бота в этот чат. Без псевдонима — автоответ.</summary>
public sealed record TelegramDelivery(Guid BotId, long ChatId, string? OperatorAlias, string Text,
    IReadOnlyList<StoredFile>? Files = null);

/// <summary>Ответ нужно доставить в WhatsApp: через этот номер компании в этот чат. Без псевдонима — автоответ.</summary>
public sealed record WhatsAppDelivery(Guid ChannelId, string ChatJid, string? OperatorAlias, string Text,
    IReadOnlyList<StoredFile>? Files = null);

/// <summary>Куда уходит ответ: канал и, для мессенджеров, через что и в какой чат.</summary>
public sealed record ReplyRoute(Channel Channel, (Guid BotId, long ChatId)? Telegram = null,
    (Guid ChannelId, string ChatJid)? WhatsApp = null);

public sealed record OperatorReply(
    OperatorMessageView Message,
    MessageView WidgetMessage,
    TicketSummary Ticket,
    WidgetRecipients Recipients,
    TelegramDelivery? Telegram,
    WhatsAppDelivery? WhatsApp = null);

/// <summary>Работа операторов с тикетами.</summary>
public sealed class TicketService(RelayoDbContext db, WidgetConversationService widgets, AttachmentService attachments,
    TimeProvider time)
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

    public const int MaxPageSize = 100;

    /// <summary>Раздел «Запросы»: все тикеты с фильтрами, новые сверху (по последнему сообщению).</summary>
    public async Task<TicketPage> SearchAsync(TicketFilter f, Guid currentUserId, int page, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);
        // Тикеты, закрытые объединением, пустые: их чаты перенесены в другой тикет.
        var q = Summaries().Where(s => s.Ticket.MergedIntoId == null);
        if (f.Statuses is { Count: > 0 } statuses)
            q = q.Where(s => statuses.Contains(s.Ticket.Status));
        if (f.Channel is { } channel)
            q = q.Where(s => s.Ticket.LastClientChannel == channel);
        if (f.Assignee == "me")
            q = q.Where(s => s.Ticket.AssigneeId == currentUserId);
        else if (f.Assignee == "none")
            q = q.Where(s => s.Ticket.AssigneeId == null);
        else if (Guid.TryParse(f.Assignee, out var assigneeId))
            q = q.Where(s => s.Ticket.AssigneeId == assigneeId);
        if (f.GroupId is { } groupId)
            q = q.Where(s => s.Ticket.GroupId == groupId);
        if (f.SiteId is { } siteId)
            q = q.Where(s => s.SiteId == siteId);
        if (f.ClientId is { } clientId)
            q = q.Where(s => s.ClientId == clientId);
        if (f.CreatedFrom is { } cf)
            q = q.Where(s => s.Ticket.CreatedAt >= cf);
        if (f.CreatedTo is { } ctTo)
            q = q.Where(s => s.Ticket.CreatedAt < ctTo);
        if (f.UpdatedFrom is { } uf)
            q = q.Where(s => s.Ticket.LastMessageAt >= uf);
        if (f.UpdatedTo is { } ut)
            q = q.Where(s => s.Ticket.LastMessageAt < ut);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var text = f.Search.Trim().TrimStart('#');
            var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
            var number = long.TryParse(text, out var n) ? n : -1;
            // Пока без полнотекстового поиска по сообщениям: номер, тема, последнее сообщение, имя и clientId клиента.
            q = q.Where(s => s.Ticket.Number == number
                             || (s.Ticket.Subject != null && EF.Functions.Like(s.Ticket.Subject, pattern, "\\"))
                             || EF.Functions.Like(s.Ticket.LastMessagePreview, pattern, "\\")
                             || (s.DisplayName != null && EF.Functions.Like(s.DisplayName, pattern, "\\"))
                             || (s.ExternalClientId != null && EF.Functions.Like(
                                 EF.Functions.Collate(s.ExternalClientId, SqlCollations.CaseInsensitive), pattern, "\\")));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(s => s.Ticket.LastMessageAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new TicketPage(rows.Select(ToSummary).ToList(), total);
    }

    /// <summary>Сотрудники, которым можно назначить запрос: не заблокированные.</summary>
    public async Task<IReadOnlyList<AgentOption>> ListAgentsAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => !u.IsBlocked).OrderBy(u => u.FullName)
            .Select(u => new AgentOption(u.Id, u.FullName, u.Alias, u.Status)).ToListAsync(ct);

    /// <summary>
    /// Статус вручную, со страницы запроса. Закрытый запрос не открывается снова: новое сообщение клиента
    /// создаст новый запрос (п. 3.2 ТЗ).
    /// </summary>
    public async Task<TicketSummary> SetStatusAsync(Guid ticketId, TicketStatus status, CancellationToken ct)
    {
        if (!Enum.IsDefined(status))
            throw new DomainException(ErrorCodes.InvalidInput, "Unknown status");
        var ticket = await FindAsync(ticketId, ct);
        if (!ticket.IsOpen && status != TicketStatus.Closed)
            throw new DomainException(ErrorCodes.TicketClosed, "A closed ticket cannot be reopened: the client's next message opens a new one");
        if (ticket.Status != status)
        {
            ticket.SetStatus(status, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        return await GetSummaryAsync(ticketId, ct);
    }

    public async Task<TicketSummary> SetSubjectAsync(Guid ticketId, string? subject, CancellationToken ct)
    {
        var ticket = await FindAsync(ticketId, ct);
        ticket.SetSubject(subject, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await GetSummaryAsync(ticketId, ct);
    }

    /// <summary>Назначить сотруднику или вернуть в очередь (null). Кто кому может назначать — решает API.</summary>
    public async Task<TicketSummary> SetAssigneeAsync(Guid ticketId, Guid? assigneeId, CancellationToken ct)
    {
        var ticket = await FindAsync(ticketId, ct);
        if (!ticket.IsOpen)
            throw new DomainException(ErrorCodes.TicketClosed, "Ticket is closed");
        if (assigneeId is { } id)
        {
            if (!await db.Users.AnyAsync(u => u.Id == id && !u.IsBlocked, ct))
                throw new DomainException(ErrorCodes.UserNotFound, "Agent not found or blocked");
            if (ticket.AssigneeId != id)
                ticket.Assign(id, time.GetUtcNow());
        }
        else if (ticket.AssigneeId is not null)
        {
            ticket.Unassign(time.GetUtcNow());
        }
        await db.SaveChangesAsync(ct);
        return await GetSummaryAsync(ticketId, ct);
    }

    /// <summary>
    /// «Взять запрос»: самый старый открытый запрос без исполнителя. Блокировка не даёт двум операторам
    /// взять один и тот же запрос. null — свободных нет.
    /// </summary>
    public async Task<TicketSummary?> TakeNextAsync(Guid operatorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync("take-next-ticket", ct);
        var ticket = await db.Tickets
            .Where(t => t.Status != TicketStatus.Closed && t.AssigneeId == null && t.MergedIntoId == null)
            .OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(ct);
        if (ticket is null)
            return null;
        ticket.Assign(operatorId, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetSummaryAsync(ticket.Id, ct);
    }

    private async Task<Ticket> FindAsync(Guid ticketId, CancellationToken ct) =>
        await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
        ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");

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
                    m.CreatedAt, m.Channel, m.Author == MessageAuthor.Operator && m.OperatorId == null, null,
                    m.Visibility))
            .Take(MessagesPerTicketLimit)
            .ToListAsync(ct);

        var files = await attachments.ForChatsAsync(chatIds, since: null, ct);
        messages = messages.Select(m => files.Contains(m.Id) ? m with { Attachments = files[m.Id].ToList() } : m).ToList();
        var byChat = messages.ToLookup(m => m.ChatId);
        var summary = ToSummary(row);
        return new TicketDetails(
            summary,
            new ClientCard(row.ClientId, summary.ClientLabel, row.SiteCode, row.ExternalClientId, row.Locale,
                row.ClientVerified),
            chats.Select(c => new ChatView(c.Id, c.IsVerified, c.CreatedAt,
                byChat[c.Id].LastOrDefault(m => m.Author == MessageAuthor.Client)?.Channel ?? Channel.Widget,
                byChat[c.Id].ToList(), c.PageUrl)).ToList());
    }

    public async Task<OperatorReply> ReplyAsync(Guid ticketId, Guid chatId, Guid operatorId, string text,
        IReadOnlyList<FileUpload> uploads, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length > Message.TextMaxLength || (text.Length == 0 && uploads.Count == 0))
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

        var route = await ReplyRouteAsync(chat.Id, ct);
        var channel = route.Channel;
        var files = await attachments.StoreAsync(uploads, ct);
        var telegram = route.Telegram is { } r ? new TelegramDelivery(r.BotId, r.ChatId, user.Alias!, text, files) : null;
        var whatsApp = route.WhatsApp is { } w ? new WhatsAppDelivery(w.ChannelId, w.ChatJid, user.Alias!, text, files) : null;

        var now = time.GetUtcNow();
        var message = Message.FromOperator(chat, operatorId, text, channel, now);
        db.Messages.Add(message);
        var views = attachments.Attach(message, files);
        chat.OnMessage(now);
        ticket.OnOperatorMessage(operatorId, AttachmentService.Preview(text, files), now);
        await db.SaveChangesAsync(ct);

        return new OperatorReply(
            new OperatorMessageView(message.Id, chat.Id, message.Author, user.FullName, message.Text, message.CreatedAt,
                message.Channel, Attachments: views),
            WidgetConversationService.ToView(message, user.Alias, views),
            await GetSummaryAsync(ticketId, ct),
            await widgets.GetRecipientsAsync(chat, ct),
            telegram,
            whatsApp);
    }

    /// <summary>
    /// Внутренняя заметка: её видят только сотрудники. Клиенту ничего не уходит, статус и превью тикета не меняются,
    /// поэтому псевдоним не нужен, а заметку можно оставить и в закрытом тикете.
    /// </summary>
    public async Task<OperatorMessageView> AddNoteAsync(Guid ticketId, Guid chatId, Guid operatorId, string text,
        IReadOnlyList<FileUpload> uploads, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length > Message.TextMaxLength || (text.Length == 0 && uploads.Count == 0))
            throw new DomainException(ErrorCodes.InvalidInput, "Note is empty or too long");

        var chat = await db.Chats.SingleOrDefaultAsync(c => c.Id == chatId && c.TicketId == ticketId, ct)
                   ?? (await db.Tickets.AnyAsync(t => t.Id == ticketId, ct)
                       ? throw new DomainException(ErrorCodes.ChatNotFound, "Chat not found in this ticket")
                       : throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found"));
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == operatorId, ct);

        var files = await attachments.StoreAsync(uploads, ct);
        var message = Message.Note(chat, operatorId, text, time.GetUtcNow());
        db.Messages.Add(message);
        var views = attachments.Attach(message, files);
        await db.SaveChangesAsync(ct);

        return new OperatorMessageView(message.Id, chat.Id, message.Author, user.FullName, message.Text,
            message.CreatedAt, message.Channel, Attachments: views, Visibility: message.Visibility);
    }

    /// <summary>Группы, в очередь которых можно перевести тикет. Список нужен всем операторам, не только администраторам.</summary>
    public async Task<IReadOnlyList<GroupOption>> ListGroupsAsync(CancellationToken ct) =>
        await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new GroupOption(g.Id, g.Name)).ToListAsync(ct);

    public async Task<TicketSummary> SetGroupAsync(Guid ticketId, Guid? groupId, CancellationToken ct)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, ct)
                     ?? throw new DomainException(ErrorCodes.TicketNotFound, "Ticket not found");
        if (groupId is { } id && !await db.AgentGroups.AnyAsync(g => g.Id == id, ct))
            throw new DomainException(ErrorCodes.GroupNotFound, "Group not found");
        if (ticket.GroupId != groupId)
        {
            ticket.SetGroup(groupId, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        return await GetSummaryAsync(ticketId, ct);
    }

    /// <summary>
    /// Куда уходит ответ в чат: в канал, из которого клиент написал последним (п. 3.2 ТЗ).
    /// Для Telegram — ещё бот и чат, для WhatsApp — номер компании и чат, через которые отправлять.
    /// </summary>
    internal async Task<ReplyRoute> ReplyRouteAsync(Guid chatId, CancellationToken ct)
    {
        var lastFromClient = await db.Messages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Author == MessageAuthor.Client)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Channel, m.VisitorIdentityId })
            .FirstOrDefaultAsync(ct);
        if (lastFromClient is { Channel: Channel.Telegram, VisitorIdentityId: { } identityId })
        {
            var identity = await db.ClientIdentities.AsNoTracking().SingleOrDefaultAsync(i => i.Id == identityId, ct);
            // Бота могли удалить или выключить: тогда ответ увидит только виджет.
            if (identity is not null && identity.TryGetTelegramChat(out var botId, out var telegramChatId)
                && await db.TelegramBots.AnyAsync(b => b.Id == botId && b.IsEnabled, ct))
                return new ReplyRoute(Channel.Telegram, Telegram: (botId, telegramChatId));
        }
        if (lastFromClient is { Channel: Channel.WhatsApp, VisitorIdentityId: { } waIdentityId })
        {
            var identity = await db.ClientIdentities.AsNoTracking().SingleOrDefaultAsync(i => i.Id == waIdentityId, ct);
            // Номер могли удалить, выключить или перевести в режим «только ссылка»: тогда ответ увидит только виджет.
            if (identity is not null && identity.TryGetWhatsAppChat(out var channelId, out var jid)
                && await db.WhatsAppChannels.AnyAsync(c => c.Id == channelId && c.IsEnabled
                    && c.Mode == Channels.WhatsAppMode.Gateway, ct))
                return new ReplyRoute(Channel.WhatsApp, WhatsApp: (channelId, jid));
        }
        return new ReplyRoute(Channel.Widget);
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
        join g in db.AgentGroups on t.GroupId equals g.Id into groups
        from g in groups.DefaultIfEmpty()
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
            SiteId = s.Id,
            AssigneeName = u == null ? null : u.FullName,
            GroupName = g == null ? null : g.Name,
        };

    private static TicketSummary ToSummary(SummaryRow r) => new(
        r.Ticket.Id, r.Ticket.Number, r.Ticket.Status, r.ClientId,
        ClientLabel(r.ClientId, r.ExternalClientId, r.DisplayName), r.ClientVerified, r.SiteCode,
        r.Ticket.AssigneeId, r.AssigneeName, r.Ticket.CreatedAt, r.Ticket.LastMessageAt, r.Ticket.LastMessagePreview,
        r.Ticket.LastClientChannel, r.Ticket.Subject, r.Ticket.Source, r.Ticket.SourceChannel, r.Ticket.GroupId,
        r.GroupName);

    /// <summary>Как клиент подписан у сотрудников: имя (введённое сотрудником или из мессенджера), иначе clientId.</summary>
    internal static string ClientLabel(Guid clientId, string? externalClientId, string? displayName) =>
        displayName ?? externalClientId ?? $"Visitor {clientId.ToString("N")[^6..]}";

    private sealed class SummaryRow
    {
        public required Ticket Ticket { get; init; }

        public Guid ClientId { get; init; }

        public string? ExternalClientId { get; init; }

        public string? DisplayName { get; init; }

        public string? Locale { get; init; }

        public bool ClientVerified { get; init; }

        public required string SiteCode { get; init; }

        public Guid SiteId { get; init; }

        public string? AssigneeName { get; init; }

        public string? GroupName { get; init; }
    }
}
