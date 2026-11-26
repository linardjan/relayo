using Microsoft.EntityFrameworkCore;
using Relayo.Core.Conversations;
using Relayo.Core.Data;

namespace Relayo.Core.Clients;

public sealed record ClientSummary(
    Guid Id,
    string Label,
    Guid SiteId,
    string SiteCode,
    string? ExternalClientId,
    string? Locale,
    bool IsVerified,
    int TicketCount,
    int OpenTicketCount,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset CreatedAt);

public sealed record ClientPage(IReadOnlyList<ClientSummary> Items, int Total);

/// <summary>Канал клиента. Токен посетителя (даже хеш) наружу не отдаётся.</summary>
public sealed record IdentityView(Guid Id, IdentityType Type, string? DisplayName, bool IsVerified, DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

public sealed record ClientChatInfo(Guid Id, bool IsVerified, DateTimeOffset CreatedAt, DateTimeOffset LastMessageAt, int MessageCount);

public sealed record ClientTicket(
    Guid Id,
    long Number,
    TicketStatus Status,
    string? AssigneeName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset LastMessageAt,
    string LastMessagePreview,
    IReadOnlyList<ClientChatInfo> Chats);

public sealed record ClientDetails(ClientSummary Client, IReadOnlyList<IdentityView> Identities, IReadOnlyList<ClientTicket> Tickets);

/// <summary>Раздел «Клиенты» админки.</summary>
public sealed class ClientService(RelayoDbContext db)
{
    public const int MaxPageSize = 100;

    public async Task<ClientPage> SearchAsync(string? search, Guid? siteId, bool includeAnonymous, int page, int pageSize,
        CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);

        var clients = db.Clients.AsNoTracking();
        if (!includeAnonymous)
            clients = clients.Where(c => c.ExternalClientId != null);
        if (siteId is { } site)
            clients = clients.Where(c => c.SiteId == site);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + EscapeLike(search.Trim()) + "%";
            clients = clients.Where(c =>
                // clientId хранится с учётом регистра (это ключ), а ищем без учёта.
                (c.ExternalClientId != null
                 && EF.Functions.Like(EF.Functions.Collate(c.ExternalClientId, SqlCollations.CaseInsensitive), pattern, "\\"))
                || (c.DisplayName != null && EF.Functions.Like(c.DisplayName, pattern, "\\")));
        }

        var total = await clients.CountAsync(ct);
        var rows = await Project(clients)
            .OrderByDescending(r => r.LastActivityAt ?? r.Client.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new ClientPage(rows.Select(ToSummary).ToList(), total);
    }

    public async Task<ClientDetails> GetAsync(Guid clientId, CancellationToken ct)
    {
        var row = await Project(db.Clients.AsNoTracking().Where(c => c.Id == clientId)).SingleOrDefaultAsync(ct)
                  ?? throw new DomainException(ErrorCodes.ClientNotFound, "Client not found");

        var identities = await db.ClientIdentities.AsNoTracking()
            .Where(i => i.ClientId == clientId)
            .OrderByDescending(i => i.LastSeenAt)
            .Select(i => new IdentityView(i.Id, i.Type, i.DisplayName, i.IsVerified, i.CreatedAt, i.LastSeenAt))
            .ToListAsync(ct);

        // Тикеты, закрытые объединением, пустые: их чаты перенесены в другой тикет.
        var tickets = await (
                from t in db.Tickets.AsNoTracking()
                where t.ClientId == clientId && t.MergedIntoId == null
                join u in db.Users on t.AssigneeId equals u.Id into assignees
                from u in assignees.DefaultIfEmpty()
                orderby t.LastMessageAt descending
                select new { Ticket = t, AssigneeName = u == null ? null : u.FullName })
            .ToListAsync(ct);

        var ticketIds = tickets.Select(t => t.Ticket.Id).ToList();
        var chats = await db.Chats.AsNoTracking()
            .Where(c => ticketIds.Contains(c.TicketId))
            .Select(c => new
            {
                c.TicketId,
                Info = new ClientChatInfo(c.Id, c.IsVerified, c.CreatedAt, c.LastMessageAt,
                    db.Messages.Count(m => m.ChatId == c.Id)),
            })
            .ToListAsync(ct);
        var chatsByTicket = chats.ToLookup(c => c.TicketId, c => c.Info);

        return new ClientDetails(
            ToSummary(row),
            identities,
            tickets.Select(t => new ClientTicket(t.Ticket.Id, t.Ticket.Number, t.Ticket.Status, t.AssigneeName,
                t.Ticket.CreatedAt, t.Ticket.ClosedAt, t.Ticket.LastMessageAt, t.Ticket.LastMessagePreview,
                chatsByTicket[t.Ticket.Id].OrderBy(c => c.CreatedAt).ToList())).ToList());
    }

    private IQueryable<ClientRow> Project(IQueryable<Client> clients) =>
        from c in clients
        join s in db.Sites on c.SiteId equals s.Id
        select new ClientRow
        {
            Client = c,
            SiteCode = s.Code,
            TicketCount = db.Tickets.Count(t => t.ClientId == c.Id && t.MergedIntoId == null),
            OpenTicketCount = db.Tickets.Count(t => t.ClientId == c.Id && t.Status != TicketStatus.Closed),
            LastActivityAt = db.Tickets.Where(t => t.ClientId == c.Id).Max(t => (DateTimeOffset?)t.LastMessageAt),
        };

    private static ClientSummary ToSummary(ClientRow r) => new(
        r.Client.Id, TicketService.ClientLabel(r.Client.Id, r.Client.ExternalClientId, r.Client.DisplayName), r.Client.SiteId, r.SiteCode,
        r.Client.ExternalClientId, r.Client.Locale, r.Client.IsVerified, r.TicketCount, r.OpenTicketCount,
        r.LastActivityAt, r.Client.CreatedAt);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private sealed class ClientRow
    {
        public required Client Client { get; init; }

        public required string SiteCode { get; init; }

        public int TicketCount { get; init; }

        public int OpenTicketCount { get; init; }

        public DateTimeOffset? LastActivityAt { get; init; }
    }
}
