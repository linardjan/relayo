using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;
using Relayo.Core.Security;

namespace Relayo.Core.Channels;

/// <summary>Подключение для админки: пароля нет, только признак, что он сохранён.</summary>
public sealed record MailServerView(string Email, string Host, int Port, MailSecurity Security, string? Username,
    bool HasPassword);

public sealed record EmailChannelView(
    Guid Id,
    string Name,
    Guid SiteId,
    string SiteCode,
    bool IsEnabled,
    bool ForwardCreatesTicket,
    EmailQuoting Quoting,
    MailServerView Imap,
    MailServerView Smtp,
    DateTimeOffset? LastCheckAt,
    bool? LastCheckOk,
    string? LastCheckMessage,
    DateTimeOffset CreatedAt);

/// <param name="Password">Пусто при изменении — оставить сохранённый пароль.</param>
public sealed record MailServerInput(string Email, string Host, int Port, MailSecurity Security, string? Username,
    string? Password);

public sealed record EmailChannelInput(
    string Name,
    Guid SiteId,
    bool IsEnabled,
    bool ForwardCreatesTicket,
    EmailQuoting Quoting,
    MailServerInput Imap,
    MailServerInput Smtp);

/// <summary>Подключение с расшифрованным паролем: только для проверки связи и работы с почтой во внутреннем сервисе.</summary>
public sealed record MailServerCredentials(string Host, int Port, MailSecurity Security, string Login, string Password);

/// <summary>Почтовые каналы (раздел «Каналы», только администратор). Подключение к серверам — во внутреннем сервисе.</summary>
public sealed class EmailChannelService(RelayoDbContext db, SecretProtector protector, TimeProvider time)
{
    public async Task<IReadOnlyList<EmailChannelView>> ListAsync(CancellationToken ct)
    {
        var rows = await (
                from c in db.EmailChannels.AsNoTracking()
                join s in db.Sites on c.SiteId equals s.Id
                orderby c.Name
                select new { Channel = c, SiteCode = s.Code })
            .ToListAsync(ct);
        return rows.Select(r => ToView(r.Channel, r.SiteCode)).ToList();
    }

    public async Task<EmailChannelView> GetAsync(Guid channelId, CancellationToken ct) =>
        (await ListAsync(ct)).SingleOrDefault(c => c.Id == channelId)
        ?? throw new DomainException(ErrorCodes.EmailChannelNotFound, "Email channel not found");

    public async Task<EmailChannelView> CreateAsync(EmailChannelInput input, CancellationToken ct)
    {
        await EnsureSiteAsync(input.SiteId, ct);
        var channel = new EmailChannel(input.Name, input.SiteId, input.IsEnabled, input.ForwardCreatesTicket, input.Quoting,
            Server("Incoming", input.Imap, null), Server("Outgoing", input.Smtp, null), time.GetUtcNow());
        await EnsureMailboxFreeAsync(channel, ct);
        db.EmailChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return await GetAsync(channel.Id, ct);
    }

    public async Task<EmailChannelView> UpdateAsync(Guid channelId, EmailChannelInput input, CancellationToken ct)
    {
        var channel = await FindAsync(channelId, ct);
        await EnsureSiteAsync(input.SiteId, ct);
        channel.Update(input.Name, input.SiteId, input.IsEnabled, input.ForwardCreatesTicket, input.Quoting);
        channel.SetServers(Server("Incoming", input.Imap, channel.Imap), Server("Outgoing", input.Smtp, channel.Smtp));
        await EnsureMailboxFreeAsync(channel, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(channel.Id, ct);
    }

    public async Task DeleteAsync(Guid channelId, CancellationToken ct)
    {
        db.EmailChannels.Remove(await FindAsync(channelId, ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Входящее и исходящее подключения с паролями — для проверки связи.</summary>
    public async Task<(MailServerCredentials Imap, MailServerCredentials Smtp)> GetCredentialsAsync(Guid channelId,
        CancellationToken ct)
    {
        var channel = await FindAsync(channelId, ct);
        return (Credentials(channel.Imap), Credentials(channel.Smtp));
    }

    public async Task<EmailChannelView> RecordCheckAsync(Guid channelId, bool ok, string message, CancellationToken ct)
    {
        var channel = await FindAsync(channelId, ct);
        channel.RecordCheck(ok, message, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await GetAsync(channelId, ct);
    }

    private async Task<EmailChannel> FindAsync(Guid channelId, CancellationToken ct) =>
        await db.EmailChannels.SingleOrDefaultAsync(c => c.Id == channelId, ct)
        ?? throw new DomainException(ErrorCodes.EmailChannelNotFound, "Email channel not found");

    /// <summary>Новый пароль шифруется; пустой — остаётся сохранённый (при создании пароль обязателен).</summary>
    private MailServer Server(string side, MailServerInput? input, MailServer? current)
    {
        if (input is null)
            throw new DomainException(ErrorCodes.InvalidInput, $"{side} connection settings are required");
        var password = !string.IsNullOrEmpty(input.Password)
            ? protector.Protect(input.Password)
            : current?.ProtectedPassword
              ?? throw new DomainException(ErrorCodes.InvalidInput, $"{side} connection: password is required");
        return MailServer.Create(side, input.Email, input.Host, input.Port, input.Security, input.Username, password);
    }

    private MailServerCredentials Credentials(MailServer server) =>
        new(server.Host, server.Port, server.Security, server.Login, protector.Unprotect(server.ProtectedPassword));

    private async Task EnsureSiteAsync(Guid siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw new DomainException(ErrorCodes.SiteNotFound, "Site not found");
    }

    /// <summary>Один ящик — один канал: иначе каждое письмо стало бы двумя тикетами.</summary>
    private async Task EnsureMailboxFreeAsync(EmailChannel channel, CancellationToken ct)
    {
        var email = channel.Imap.Email;
        if (await db.EmailChannels.AnyAsync(c => c.Imap.Email == email && c.Id != channel.Id, ct))
            throw new DomainException(ErrorCodes.MailboxTaken, $"The mailbox {email} is already connected");
    }

    private static MailServerView View(MailServer s) => new(s.Email, s.Host, s.Port, s.Security, s.Username, true);

    private static EmailChannelView ToView(EmailChannel c, string siteCode) =>
        new(c.Id, c.Name, c.SiteId, siteCode, c.IsEnabled, c.ForwardCreatesTicket, c.Quoting, View(c.Imap), View(c.Smtp),
            c.LastCheckAt, c.LastCheckOk, c.LastCheckMessage, c.CreatedAt);
}
