using Microsoft.EntityFrameworkCore;
using Relayo.Core.Conversations;
using Relayo.Core.Data;
using Relayo.Core.Files;
using Relayo.Core.Sites;

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
    DateTimeOffset CreatedAt,
    string? DisplayName = null,
    string? Position = null,
    string? Note = null,
    bool IsVip = false,
    bool IsSpammer = false,
    string? AvatarUrl = null);

/// <summary>Сайт для выбора при создании клиента: без ключей и настроек, их видит только администратор.</summary>
public sealed record ClientSiteOption(Guid Id, string Code, string Name);

/// <summary>Карточка клиента, которую заполняют сотрудники. Пустые строки — «не указано».</summary>
public sealed record ClientProfileInput(string? Name, string? Locale, string? Position, string? Note, bool IsVip, bool IsSpammer);

/// <param name="ExternalClientId">clientId сайта. Пусто — клиент без clientId (найдётся по каналам).</param>
public sealed record NewClientInput(Guid SiteId, string? ExternalClientId, ClientProfileInput Profile);

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
    IReadOnlyList<ClientChatInfo> Chats,
    string? Subject = null,
    Channel Channel = Channel.Widget);

public sealed record ClientDetails(ClientSummary Client, IReadOnlyList<IdentityView> Identities, IReadOnlyList<ClientTicket> Tickets);

/// <summary>Раздел «Клиенты» админки.</summary>
public sealed class ClientService(RelayoDbContext db, IFileStorage storage, TimeProvider time)
{
    public const int MaxPageSize = 100;
    public const int AvatarMaxBytes = 2 * 1024 * 1024;

    private static readonly string[] AvatarTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public async Task<IReadOnlyList<ClientSiteOption>> SitesAsync(CancellationToken ct) =>
        await db.Sites.AsNoTracking().OrderBy(s => s.Name).Select(s => new ClientSiteOption(s.Id, s.Code, s.Name)).ToListAsync(ct);

    /// <summary>Клиент, заведённый сотрудником вручную. С clientId — тот же, кого потом передаст сайт.</summary>
    public async Task<ClientDetails> CreateAsync(NewClientInput input, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == input.SiteId, ct))
            throw new DomainException(ErrorCodes.SiteNotFound, "Site not found");
        var externalId = input.ExternalClientId?.Trim();
        Client client;
        if (string.IsNullOrEmpty(externalId))
        {
            client = Client.CreateAnonymous(input.SiteId, time.GetUtcNow());
        }
        else
        {
            if (externalId.Length > Client.ExternalIdMaxLength)
                throw new DomainException(ErrorCodes.InvalidInput, $"clientId must be up to {Client.ExternalIdMaxLength} characters");
            if (await db.Clients.AnyAsync(c => c.SiteId == input.SiteId && c.ExternalClientId == externalId, ct))
                throw new DomainException(ErrorCodes.ClientIdTaken, "A client with this clientId already exists on this site");
            client = Client.CreateIdentified(input.SiteId, externalId, time.GetUtcNow());
        }
        Apply(client, input.Profile);
        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);
        return await GetAsync(client.Id, ct);
    }

    public async Task<ClientDetails> UpdateAsync(Guid clientId, ClientProfileInput input, CancellationToken ct)
    {
        var client = await FindAsync(clientId, ct);
        Apply(client, input);
        await db.SaveChangesAsync(ct);
        return await GetAsync(clientId, ct);
    }

    /// <summary>Аватар — в закрытом бакете вложений: это персональные данные, показываются по временной ссылке.</summary>
    public async Task<ClientDetails> SetAvatarAsync(Guid clientId, FileUpload upload, CancellationToken ct)
    {
        var client = await FindAsync(clientId, ct);
        if (!storage.IsConfigured)
            throw new DomainException(ErrorCodes.StorageNotConfigured, "File storage (S3) is not configured");
        if (upload.Content.Length > AvatarMaxBytes)
            throw new DomainException(ErrorCodes.AttachmentTooLarge, "The image must be up to 2 MB");
        var file = AttachmentPolicy.Check([upload], new AttachmentLimits(AvatarMaxBytes, AvatarTypes)).Single();

        var extension = file.ContentType.Split('/')[1].Replace("jpeg", "jpg");
        var key = $"client-avatars/{client.Id:N}/{WidgetKeys.Create()}.{extension}";
        await storage.PutAttachmentAsync(key, file.Content, file.ContentType, ct);
        var previous = client.AvatarKey;
        client.SetAvatar(key);
        await db.SaveChangesAsync(ct);
        if (previous is not null)
            await storage.DeleteAttachmentAsync(previous, ct);
        return await GetAsync(clientId, ct);
    }

    public async Task<ClientDetails> RemoveAvatarAsync(Guid clientId, CancellationToken ct)
    {
        var client = await FindAsync(clientId, ct);
        if (client.AvatarKey is { } key)
        {
            client.SetAvatar(null);
            await db.SaveChangesAsync(ct);
            await storage.DeleteAttachmentAsync(key, ct);
        }
        return await GetAsync(clientId, ct);
    }

    /// <summary>
    /// Удаляет клиента без переписки (например, заведённого по ошибке) вместе с его каналами. Клиента с тикетами
    /// удалить нельзя: история обращений нужна, а объединение дублей — отдельная функция.
    /// </summary>
    public async Task DeleteAsync(Guid clientId, CancellationToken ct)
    {
        var client = await FindAsync(clientId, ct);
        if (await db.Tickets.AnyAsync(t => t.ClientId == clientId, ct))
            throw new DomainException(ErrorCodes.HasHistory, "The client has tickets and cannot be deleted");
        db.ClientIdentities.RemoveRange(await db.ClientIdentities.Where(i => i.ClientId == clientId).ToListAsync(ct));
        db.Clients.Remove(client);
        await db.SaveChangesAsync(ct);
        if (client.AvatarKey is { } key && storage.IsConfigured)
            await storage.DeleteAttachmentAsync(key, ct);
    }

    private static void Apply(Client client, ClientProfileInput? input)
    {
        if (input is null)
            throw new DomainException(ErrorCodes.InvalidInput, "Client details are required");
        client.SetProfile(input.Name, input.Locale, input.Position, input.Note, input.IsVip, input.IsSpammer);
    }

    private async Task<Client> FindAsync(Guid clientId, CancellationToken ct) =>
        await db.Clients.SingleOrDefaultAsync(c => c.Id == clientId, ct)
        ?? throw new DomainException(ErrorCodes.ClientNotFound, "Client not found");

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
                chatsByTicket[t.Ticket.Id].OrderBy(c => c.CreatedAt).ToList(), t.Ticket.Subject, t.Ticket.LastClientChannel)).ToList());
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

    private ClientSummary ToSummary(ClientRow r) => new(
        r.Client.Id, TicketService.ClientLabel(r.Client.Id, r.Client.ExternalClientId, r.Client.DisplayName), r.Client.SiteId, r.SiteCode,
        r.Client.ExternalClientId, r.Client.Locale, r.Client.IsVerified, r.TicketCount, r.OpenTicketCount,
        r.LastActivityAt, r.Client.CreatedAt, r.Client.DisplayName, r.Client.Position, r.Client.Note, r.Client.IsVip,
        r.Client.IsSpammer, r.Client.AvatarKey is { } key ? storage.AttachmentUrl(key, "avatar", AvatarContentType(key)) : null);

    private static string AvatarContentType(string key) => Path.GetExtension(key) switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

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
