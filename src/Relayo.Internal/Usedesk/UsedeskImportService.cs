using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Core.Data;
using Relayo.Core.Security;
using Relayo.Core.Sites;
using Relayo.Core.Usedesk;
using Relayo.Core.Users;
using Relayo.Internal.Channels.Telegram;

namespace Relayo.Internal.Usedesk;

/// <summary>Подключение для админки: пароля нет, только признак, что он сохранён.</summary>
public sealed record UsedeskConnectionView(string Host, int Port, string Database, string Username, bool HasPassword, int CompanyId,
    DateTimeOffset? LastCheckAt, bool? LastCheckOk, string? LastCheckMessage);

/// <param name="Password">Пусто — оставить сохранённый.</param>
public sealed record UsedeskConnectionInput(string Host, int Port, string Database, string Username, string? Password, int CompanyId);

public enum UsedeskImportKind
{
    Users,
    Email,
    Telegram,
    WhatsApp,
    Widgets,
    Groups,
}

/// <summary>
/// Строка предпросмотра. <paramref name="ImportedId"/> — что уже создано в Relayo; <paramref name="Problem"/> — почему
/// перенести нельзя; <paramref name="Notes"/> — что перенесётся не полностью.
/// </summary>
public sealed record UsedeskPreviewRow(int Id, string Name, string Details, bool Deleted, Guid? ImportedId, string? Problem,
    IReadOnlyList<string> Notes);

public sealed record UsedeskPreview(
    IReadOnlyList<UsedeskPreviewRow> Users,
    IReadOnlyList<UsedeskPreviewRow> Email,
    IReadOnlyList<UsedeskPreviewRow> Telegram,
    IReadOnlyList<UsedeskPreviewRow> WhatsApp,
    IReadOnlyList<UsedeskPreviewRow> Widgets,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<UsedeskPreviewRow> Groups);

/// <param name="SiteId">Сайт для каналов и виджетов (клиенты, которые туда пишут, относятся к нему). Для групп и пользователей не нужен.</param>
public sealed record UsedeskImportRequest(UsedeskImportKind Kind, IReadOnlyList<int> Ids, Guid? SiteId,
    IdentificationMode? IdentificationMode = null);

public sealed record UsedeskImportProblem(int Id, string Name, string Reason);

/// <param name="GroupsAssigned">Скольким перенесённым сотрудникам добавлены группы (после импорта групп и сотрудников).</param>
public sealed record UsedeskImportResult(int Imported, IReadOnlyList<UsedeskImportProblem> Skipped, int GroupsAssigned = 0);

/// <summary>
/// Временный импорт настроек из Usedesk (docs/usedesk-migration.md): группы, пользователи, почтовые каналы, Telegram-боты,
/// номера WhatsApp и виджеты. Создаёт их тем же кодом, что админка; повторный импорт ничего не дублирует
/// (usedesk_id у пользователей, usedesk_channel_id у каналов).
/// </summary>
public sealed class UsedeskImportService(
    RelayoDbContext db,
    UsedeskSource source,
    SecretProtector protector,
    EmailChannelService email,
    WhatsAppChannelService whatsApp,
    SiteSettingsService sites,
    TelegramBotSettingsService telegram,
    TimeProvider time,
    ILogger<UsedeskImportService> logger)
{
    public async Task<UsedeskConnectionView?> GetConnectionAsync(CancellationToken ct) =>
        await db.UsedeskConnections.AsNoTracking().SingleOrDefaultAsync(ct) is { } c ? ToView(c) : null;

    /// <summary>Сохраняет подключение и сразу проверяет его: результат виден на той же странице.</summary>
    public async Task<UsedeskConnectionView> SaveConnectionAsync(UsedeskConnectionInput input, CancellationToken ct)
    {
        var connection = await db.UsedeskConnections.SingleOrDefaultAsync(ct);
        if (connection is null)
        {
            connection = new UsedeskConnection(time.GetUtcNow());
            db.UsedeskConnections.Add(connection);
        }
        connection.Configure(input.Host, input.Port, input.Database, input.Username, input.CompanyId, time.GetUtcNow());
        if (!string.IsNullOrEmpty(input.Password))
            connection.SetPassword(protector.Protect(input.Password));
        if (connection.ProtectedPassword is null)
            throw new DomainException(ErrorCodes.InvalidInput, "Password is required");
        await db.SaveChangesAsync(ct);
        return await CheckAsync(ct);
    }

    public async Task<UsedeskConnectionView> CheckAsync(CancellationToken ct)
    {
        var connection = await LoadAsync(ct);
        try
        {
            connection.RecordCheck(true, await source.CheckAsync(Login(connection), ct), time.GetUtcNow());
        }
        catch (Exception ex) when (ex is DomainException or MySqlException)
        {
            logger.LogWarning("Usedesk connection check failed: {Error}", ex.Message);
            connection.RecordCheck(false, ex.Message, time.GetUtcNow());
        }
        await db.SaveChangesAsync(ct);
        return ToView(connection);
    }

    /// <summary>Удаляет подключение вместе с паролем. Перенесённое остаётся.</summary>
    public async Task ForgetAsync(CancellationToken ct)
    {
        if (await db.UsedeskConnections.SingleOrDefaultAsync(ct) is { } connection)
        {
            db.UsedeskConnections.Remove(connection);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<UsedeskPreview> PreviewAsync(CancellationToken ct)
    {
        var data = await ReadAsync(ct);
        var warnings = new List<string>();
        if (!data.GuidTableFound)
            warnings.Add("Table users_guid_migration was not found: users are matched with AD only by email");

        var users = await db.Users.AsNoTracking().Where(u => u.UsedeskId != null).ToDictionaryAsync(u => u.UsedeskId!.Value, u => u.Id, ct);
        var emails = await Linked(db.EmailChannels.Select(c => new { c.UsedeskChannelId, c.Id }).ToListAsync(ct), x => x.UsedeskChannelId, x => x.Id);
        var bots = await Linked(db.TelegramBots.Select(c => new { c.UsedeskChannelId, c.Id }).ToListAsync(ct), x => x.UsedeskChannelId, x => x.Id);
        var numbers = await Linked(db.WhatsAppChannels.Select(c => new { c.UsedeskChannelId, c.Id }).ToListAsync(ct), x => x.UsedeskChannelId, x => x.Id);
        var widgets = await Linked(db.Widgets.Select(c => new { c.UsedeskChannelId, c.Id }).ToListAsync(ct), x => x.UsedeskChannelId, x => x.Id);
        var channelName = data.Channels.ToDictionary(c => c.Id, c => c.Name);
        var linkedGroups = await db.AgentGroups.AsNoTracking().Where(g => g.UsedeskId != null)
            .ToDictionaryAsync(g => g.UsedeskId!.Value, g => g.Id, ct);
        var groupNames = await db.AgentGroups.AsNoTracking().Select(g => new { g.Name, g.UsedeskId }).ToListAsync(ct);
        var usedeskGroup = data.Groups.ToDictionary(g => g.Id);

        var groupRows = data.Groups.Select(g =>
        {
            var (name, problem) = UsedeskMapping.Group(g);
            var members = data.Users.Count(u => UsedeskMapping.Role(u.Role) is not null && (u.GroupId == g.Id || u.DefaultGroupId == g.Id));
            var notes = new List<string>();
            if (name is not null && !linkedGroups.ContainsKey(g.Id))
            {
                // Имя группы в Relayo уникально (без учёта регистра, как сравнивает SQL Server).
                var twin = data.Groups.FirstOrDefault(o => o.Id < g.Id && UsedeskMapping.Group(o).Name is { } n
                    && string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                var existing = groupNames.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (twin is not null)
                    problem = $"Same name as group #{twin.Id}: rename one of them in Usedesk";
                else if (existing is { UsedeskId: { } other })
                    problem = $"A group with this name is already linked to Usedesk group #{other}";
                else if (existing is not null)
                    notes.Add("A group with this name exists in Relayo: it is linked, not created again");
                if (name != g.Name.Trim())
                    notes.Add($"Name shortened to {AgentGroup.NameMaxLength} characters");
            }
            if (problem is null)
            {
                if (g.CustomWorkingTime)
                    notes.Add("Own working hours are not imported");
                if (!g.PersonalDataAccess)
                    notes.Add("Client personal data is hidden from this group in Usedesk: Relayo has no such restriction");
            }
            return new UsedeskPreviewRow(g.Id, name ?? g.Name, members == 1 ? "1 agent" : $"{members} agents", g.Deleted, Get(linkedGroups, g.Id), problem, notes);
        }).ToList();

        var userRows = data.Users.Select(u =>
        {
            var role = UsedeskMapping.Role(u.Role);
            // У тех, кого не переносим (support, bot), пояснения не нужны: причина — в Problem.
            var notes = new List<string>();
            if (role is not null)
            {
                if (UsedeskMapping.ParseGuid(u.Guid) is not null)
                    notes.Add("AD account: linked by objectGUID");
                else if (u.Guid is not null)
                    notes.Add("objectGUID has an unknown format: matched by email");
                else
                    notes.Add("No AD account: a local user without a password (set it on the agent page)");
                if (u.Deleted || !u.Active)
                    notes.Add("Removed in Usedesk: imported blocked to keep the history");
                // Группы сотрудника: уже перенесённые, ждущие импорта групп и те, что не переносятся (удалённые, демо).
                var groupIds = new[] { u.GroupId, u.DefaultGroupId }.OfType<int>().Distinct().ToList();
                string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => usedeskGroup.TryGetValue(id, out var g) ? g.Name : $"#{id}"));
                var linked = groupIds.Where(linkedGroups.ContainsKey).ToList();
                var skippedGroups = groupIds.Where(id => !linkedGroups.ContainsKey(id)
                    && (!usedeskGroup.TryGetValue(id, out var g) || UsedeskMapping.Group(g).Problem is not null)).ToList();
                var pending = groupIds.Except(linked).Except(skippedGroups).ToList();
                if (linked.Count > 0)
                    notes.Add($"Groups: {Names(linked)}");
                if (pending.Count > 0)
                    notes.Add($"Groups: {Names(pending)} (added when the groups are imported)");
                if (skippedGroups.Count > 0)
                    notes.Add($"Groups not imported (deleted, demo or missing in Usedesk): {Names(skippedGroups)}");
            }
            return new UsedeskPreviewRow(u.Id, u.Name, $"{u.Email} · {u.Role}", u.Deleted, Get(users, u.Id),
                role is null ? "Usedesk support accounts and bots are not imported" : null, notes);
        }).ToList();

        var emailRows = data.Channels.Where(IsMail).Select(c =>
        {
            var (_, problem) = UsedeskMapping.EmailChannel(c, Guid.Empty);
            var details = c.ImapHost is null ? c.IncomingConnection : $"{UsedeskMapping.MailboxAddress(c)} · IMAP {c.ImapHost}:{c.ImapPort} · SMTP {c.SmtpHost}:{c.SmtpPort}";
            return new UsedeskPreviewRow(c.Id, c.Name, details, c.Deleted, Get(emails, c.Id), problem,
                ["Check the connection on the channel page after the import"]);
        }).ToList();

        var telegramRows = data.Telegram.Select(t =>
        {
            var name = t.ChannelId is { } cid && channelName.TryGetValue(cid, out var n) ? n : $"Telegram {t.Id}";
            var deleted = t.ChannelId is { } c2 && data.Channels.FirstOrDefault(c => c.Id == c2) is { Deleted: true };
            // В токене публична только часть до двоеточия — id бота; сам токен не показываем.
            var botId = t.Token?.Split(':')[0];
            var problem = t.ChannelId is null ? "No Usedesk channel" : string.IsNullOrWhiteSpace(t.Token) ? "No token" : null;
            return new UsedeskPreviewRow(t.Id, name, botId is { Length: > 0 } ? $"bot id {botId}" : "", deleted,
                t.ChannelId is { } c3 ? Get(bots, c3) : null, problem,
                ["Imported disabled: the bot keeps working in Usedesk until you enable it in Relayo"]);
        }).ToList();

        var whatsAppRows = data.Pact.Select(p =>
        {
            var name = p.ChannelId is { } cid && channelName.TryGetValue(cid, out var n) ? n : p.AccountName ?? $"Pact {p.Id}";
            var isWhatsApp = p.ProviderType is null || p.ProviderType.Contains("whatsapp", StringComparison.OrdinalIgnoreCase);
            var notes = new List<string> { "Scan the QR code in Relayo, then unlink the Pact device on the phone" };
            if (p.GroupChats)
                notes.Add("Group chats are not supported yet");
            return new UsedeskPreviewRow(p.Id, name, string.Join(" · ", new[] { p.ProviderType, p.AccountName, p.Active ? null : "inactive" }.Where(x => x is not null)),
                false, p.ChannelId is { } c ? Get(numbers, c) : null,
                p.ChannelId is null ? "No Usedesk channel" : isWhatsApp ? null : $"{p.ProviderType} via Pact is not available in Relayo: history only",
                notes);
        }).ToList();

        var widgetRows = data.Widgets.Select(w =>
        {
            var m = data.Messengers.FirstOrDefault(x => x.ChannelId == w.ChannelId);
            var mapping = UsedeskMapping.Widget(w, m, IdentificationMode.SignedToken);
            var notes = mapping.Notes.ToList();
            if (mapping.TelegramUsername is { } username)
                notes.Insert(0, $"Telegram item: bot @{username} (import it first)");
            if (mapping.WhatsAppPhone is { } phone)
                notes.Insert(0, $"WhatsApp item: +{phone} — choose the number on the widget page after it is connected");
            var name = mapping.Input.Name;
            return new UsedeskPreviewRow(w.Id, name, $"{mapping.Input.Title} · {w.Language} · {mapping.Input.Options!.Items.Count} items", false,
                w.ChannelId is { } c ? Get(widgets, c) : null, w.ChannelId is null ? "The widget has no Usedesk channel" : null, notes);
        }).ToList();

        return new UsedeskPreview(userRows, emailRows, telegramRows, whatsAppRows, widgetRows, warnings, groupRows);
    }

    public async Task<UsedeskImportResult> ImportAsync(UsedeskImportRequest request, CancellationToken ct)
    {
        if (request.Ids is not { Count: > 0 })
            throw new DomainException(ErrorCodes.InvalidInput, "Select what to import");
        if (request.Kind is not (UsedeskImportKind.Users or UsedeskImportKind.Groups) && request.SiteId is null)
            throw new DomainException(ErrorCodes.InvalidInput, "Choose the site for the imported channels");
        var data = await ReadAsync(ct);
        var ids = request.Ids.ToHashSet();
        var skipped = new List<UsedeskImportProblem>();
        var imported = 0;

        async Task Run(int id, string name, Func<Task> import)
        {
            try
            {
                await import();
                imported++;
                // Каждая запись уже сохранена. Импорт «всего» — это сотни записей: без очистки каждое следующее
                // сохранение перебирало бы все созданные раньше сущности.
                db.ChangeTracker.Clear();
            }
            catch (Exception ex) when (ex is DomainException or DbUpdateException)
            {
                // Не сохранённое из-за ошибки не должно уйти со следующей записью.
                db.ChangeTracker.Clear();
                skipped.Add(new UsedeskImportProblem(id, name, ex is DomainException ? ex.Message : "Could not save: it may already exist"));
            }
        }

        var siteId = request.SiteId ?? Guid.Empty;
        var groupsAssigned = 0;
        switch (request.Kind)
        {
            case UsedeskImportKind.Groups:
                foreach (var g in data.Groups.Where(g => ids.Contains(g.Id)))
                    await Run(g.Id, g.Name, () => ImportGroupAsync(g, data.Groups, ct));
                // Сотрудников могли перенести раньше групп: группы добавляются им сейчас.
                groupsAssigned = await AssignGroupsAsync(data.Users, ct);
                break;
            case UsedeskImportKind.Users:
                foreach (var u in data.Users.Where(u => ids.Contains(u.Id)))
                    await Run(u.Id, u.Name, () => ImportUserAsync(u, ct));
                groupsAssigned = await AssignGroupsAsync(data.Users, ct);
                break;
            case UsedeskImportKind.Email:
                foreach (var c in data.Channels.Where(c => ids.Contains(c.Id) && IsMail(c)))
                    await Run(c.Id, c.Name, async () =>
                    {
                        await EnsureNewAsync(db.EmailChannels.AnyAsync(x => x.UsedeskChannelId == c.Id, ct));
                        var (input, problem) = UsedeskMapping.EmailChannel(c, siteId);
                        if (input is null)
                            throw new DomainException(ErrorCodes.InvalidInput, problem!);
                        var created = await email.CreateAsync(input, ct);
                        (await db.EmailChannels.SingleAsync(x => x.Id == created.Id, ct)).LinkUsedeskChannel(c.Id);
                        await db.SaveChangesAsync(ct);
                    });
                break;
            case UsedeskImportKind.Telegram:
                foreach (var t in data.Telegram.Where(t => ids.Contains(t.Id)))
                {
                    var name = data.Channels.FirstOrDefault(c => c.Id == t.ChannelId)?.Name ?? $"Telegram {t.Id}";
                    await Run(t.Id, name, async () =>
                    {
                        if (t.ChannelId is not { } channelId || string.IsNullOrWhiteSpace(t.Token))
                            throw new DomainException(ErrorCodes.InvalidInput, "No Usedesk channel or token");
                        await EnsureNewAsync(db.TelegramBots.AnyAsync(x => x.UsedeskChannelId == channelId, ct));
                        await telegram.ImportDisabledAsync(UsedeskMapping.Clip(name, TelegramBot.NameMaxLength), siteId, t.Token, channelId, ct);
                    });
                }
                break;
            case UsedeskImportKind.WhatsApp:
                foreach (var p in data.Pact.Where(p => ids.Contains(p.Id)))
                {
                    var name = data.Channels.FirstOrDefault(c => c.Id == p.ChannelId)?.Name ?? p.AccountName ?? $"WhatsApp {p.Id}";
                    await Run(p.Id, name, async () =>
                    {
                        if (p.ChannelId is not { } channelId)
                            throw new DomainException(ErrorCodes.InvalidInput, "No Usedesk channel");
                        if (p.ProviderType is { } provider && !provider.Contains("whatsapp", StringComparison.OrdinalIgnoreCase))
                            throw new DomainException(ErrorCodes.InvalidInput, $"{provider} via Pact is not available in Relayo");
                        await EnsureNewAsync(db.WhatsAppChannels.AnyAsync(x => x.UsedeskChannelId == channelId, ct));
                        var created = await whatsApp.CreateAsync(new WhatsAppChannelInput(
                            UsedeskMapping.Clip(name, WhatsAppChannel.NameMaxLength), WhatsAppMode.Gateway, null, siteId, true), ct);
                        (await db.WhatsAppChannels.SingleAsync(x => x.Id == created.Id, ct)).LinkUsedeskChannel(channelId);
                        await db.SaveChangesAsync(ct);
                    });
                }
                break;
            case UsedeskImportKind.Widgets:
                foreach (var w in data.Widgets.Where(w => ids.Contains(w.Id)))
                {
                    var mapping = UsedeskMapping.Widget(w, data.Messengers.FirstOrDefault(m => m.ChannelId == w.ChannelId),
                        request.IdentificationMode ?? IdentificationMode.SignedToken);
                    await Run(w.Id, mapping.Input.Name, async () =>
                    {
                        if (w.ChannelId is not { } channelId)
                            throw new DomainException(ErrorCodes.InvalidInput, "The widget has no Usedesk channel");
                        await EnsureNewAsync(db.Widgets.AnyAsync(x => x.UsedeskChannelId == channelId, ct));
                        // Бот и номер подключаются, если они уже в Relayo: бот — по имени из ссылки, номер — по цифрам.
                        var botId = mapping.TelegramUsername is { } username
                            ? await db.TelegramBots.Where(b => b.Username == username).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct)
                            : null;
                        var numberId = mapping.WhatsAppPhone is { } phone
                            ? await db.WhatsAppChannels.Where(c => c.PhoneNumber == phone).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
                            : null;
                        var created = await sites.CreateWidgetAsync(siteId, mapping.Input with { TelegramBotId = botId, WhatsAppChannelId = numberId }, ct);
                        (await db.Widgets.SingleAsync(x => x.Id == created.Id, ct)).LinkUsedeskChannel(channelId);
                        await db.SaveChangesAsync(ct);
                    });
                }
                break;
            default:
                throw new DomainException(ErrorCodes.InvalidInput, "Unknown import section");
        }

        logger.LogInformation("Usedesk import {Kind}: imported {Imported}, skipped {Skipped}, groups assigned to {GroupsAssigned} agents",
            request.Kind, imported, skipped.Count, groupsAssigned);
        return new UsedeskImportResult(imported, skipped, groupsAssigned);
    }

    /// <summary>
    /// Сотрудник Usedesk. Сначала ищется уже заведённый в Relayo: по objectGUID (импорт из AD), затем по почте и логину.
    /// Нашёлся — к нему только привязывается id Usedesk. Нет — создаётся: с GUID — как пользователь AD (синхронизация
    /// свяжет его, а не создаст второго), без — локальным без пароля. Псевдоним — имя, которое видели клиенты в Usedesk.
    /// </summary>
    private async Task ImportUserAsync(UsedeskUser u, CancellationToken ct)
    {
        if (UsedeskMapping.Role(u.Role) is not { } role)
            throw new DomainException(ErrorCodes.InvalidInput, "Usedesk support accounts and bots are not imported");
        await EnsureNewAsync(db.Users.AnyAsync(x => x.UsedeskId == u.Id, ct));
        var email = u.Email.Trim();
        if (email.Length is 0 or > User.LoginMaxLength || email.Any(char.IsWhiteSpace))
            throw new DomainException(ErrorCodes.InvalidInput, "The user has no valid email to use as a login");

        var guid = UsedeskMapping.ParseGuid(u.Guid);
        var login = User.NormalizeLogin(email);
        var user = (guid is { } g ? await db.Users.SingleOrDefaultAsync(x => x.AdObjectGuid == g, ct) : null)
                   ?? await db.Users.FirstOrDefaultAsync(x => x.LoginNormalized == login || x.Email == email, ct);
        if (user is not null)
        {
            if (user.UsedeskId is not null)
                throw new DomainException(ErrorCodes.InvalidInput, $"{user.Login} is already linked to another Usedesk user");
        }
        else
        {
            var name = UsedeskMapping.Clip(u.Name, User.FullNameMaxLength) is { Length: > 0 } n ? n : email;
            user = guid is { } adGuid
                ? User.CreateFromDirectory(email, name, role, adGuid, time.GetUtcNow())
                : User.CreateLocal(email, name, role, alias: null, time.GetUtcNow());
            user.SetProfile(email, u.Phone, u.Position, UsedeskMapping.Clip(u.Lang, 16) is { Length: > 0 } l ? l : "ru");
            if (u.Deleted || !u.Active)
                user.Block();
            db.Users.Add(user);
        }
        if (user.Alias is null && UsedeskMapping.Clip(u.Name, User.AliasMaxLength) is { Length: > 0 } alias)
            user.SetAlias(alias);
        user.LinkUsedesk(u.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Группа Usedesk. Группа с тем же именем, уже заведённая в Relayo вручную, только привязывается: имя у нас уникально,
    /// а второй такой отдел не нужен.
    /// </summary>
    private async Task ImportGroupAsync(UsedeskGroup g, IReadOnlyList<UsedeskGroup> all, CancellationToken ct)
    {
        var (name, problem) = UsedeskMapping.Group(g);
        if (name is null)
            throw new DomainException(ErrorCodes.InvalidInput, problem!);
        await EnsureNewAsync(db.AgentGroups.AnyAsync(x => x.UsedeskId == g.Id, ct));
        if (all.FirstOrDefault(o => o.Id < g.Id && string.Equals(UsedeskMapping.Group(o).Name, name, StringComparison.OrdinalIgnoreCase)) is { } twin)
            throw new DomainException(ErrorCodes.InvalidInput, $"Same name as group #{twin.Id}: rename one of them in Usedesk");

        var group = await db.AgentGroups.SingleOrDefaultAsync(x => x.Name == name, ct);
        if (group is { UsedeskId: { } other })
            throw new DomainException(ErrorCodes.InvalidInput, $"A group with this name is already linked to Usedesk group #{other}");
        if (group is null)
        {
            group = new AgentGroup(name, time.GetUtcNow());
            db.AgentGroups.Add(group);
        }
        group.LinkUsedesk(g.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Добавляет перенесённым сотрудникам их группы из Usedesk, если группы уже перенесены. Только добавляет: группы,
    /// назначенные в Relayo вручную, остаются. Возвращает, скольким сотрудникам что-то добавлено.
    /// </summary>
    private async Task<int> AssignGroupsAsync(IReadOnlyList<UsedeskUser> usedeskUsers, CancellationToken ct)
    {
        var withGroups = usedeskUsers.Where(u => u.GroupId is not null || u.DefaultGroupId is not null).ToDictionary(u => u.Id);
        if (withGroups.Count == 0)
            return 0;
        var groups = await db.AgentGroups.AsNoTracking().Where(g => g.UsedeskId != null)
            .ToDictionaryAsync(g => g.UsedeskId!.Value, g => g.Id, ct);
        if (groups.Count == 0)
            return 0;
        var ids = withGroups.Keys.ToList();
        var users = await db.Users.Where(u => u.UsedeskId != null && ids.Contains(u.UsedeskId.Value)).ToListAsync(ct);
        var changed = 0;
        foreach (var user in users)
        {
            if (UsedeskMapping.UserGroups(withGroups[user.UsedeskId!.Value], groups, user.GroupIds, user.DefaultGroupId) is { } set)
            {
                user.SetGroups(set.GroupIds, set.DefaultGroupId);
                changed++;
            }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return changed;
    }

    private static async Task EnsureNewAsync(Task<bool> exists)
    {
        if (await exists)
            throw new DomainException(ErrorCodes.InvalidInput, "Already imported");
    }

    private static bool IsMail(UsedeskChannel c) =>
        c.ImapHost is not null || string.Equals(c.Type, "email", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<int, Guid>> Linked<T>(Task<List<T>> rows, Func<T, int?> key, Func<T, Guid> id) =>
        (await rows).Where(r => key(r) is not null).GroupBy(r => key(r)!.Value).ToDictionary(g => g.Key, g => id(g.First()));

    private static Guid? Get(Dictionary<int, Guid> map, int key) => map.TryGetValue(key, out var id) ? id : null;

    private async Task<UsedeskSnapshot> ReadAsync(CancellationToken ct)
    {
        var connection = await LoadAsync(ct);
        try
        {
            return await source.ReadAsync(Login(connection), ct);
        }
        catch (MySqlException ex)
        {
            throw new DomainException(ErrorCodes.UsedeskUnavailable, $"Could not read the Usedesk database: {ex.Message}");
        }
    }

    private async Task<UsedeskConnection> LoadAsync(CancellationToken ct)
    {
        var connection = await db.UsedeskConnections.SingleOrDefaultAsync(ct);
        if (connection is not { IsConfigured: true })
            throw new DomainException(ErrorCodes.InvalidInput, "Fill in and save the Usedesk connection first");
        return connection;
    }

    private UsedeskLogin Login(UsedeskConnection c) =>
        new(c.Host, c.Port, c.Database, c.Username, protector.Unprotect(c.ProtectedPassword!), c.CompanyId);

    private static UsedeskConnectionView ToView(UsedeskConnection c) =>
        new(c.Host, c.Port, c.Database, c.Username, c.ProtectedPassword is not null, c.CompanyId, c.LastCheckAt, c.LastCheckOk,
            c.LastCheckMessage);
}
