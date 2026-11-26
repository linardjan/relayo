using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;
using Relayo.Core.Sites;

namespace Relayo.Core.Channels;

public sealed record WhatsAppChannelView(
    Guid Id,
    string Name,
    WhatsAppMode Mode,
    string? PhoneNumber,
    Guid SiteId,
    string SiteCode,
    bool IsEnabled,
    bool IsConnected,
    string? AccountName,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset CreatedAt,
    int WidgetCount);

/// <param name="PhoneNumber">Обязателен в режиме «только ссылка»; в режиме QR номер приходит из WhatsApp.</param>
public sealed record WhatsAppChannelInput(string Name, WhatsAppMode Mode, string? PhoneNumber, Guid SiteId, bool IsEnabled);

/// <summary>Каналы WhatsApp (раздел «Каналы», только администратор). Связь со шлюзом — во внутреннем сервисе.</summary>
public sealed class WhatsAppChannelService(RelayoDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<WhatsAppChannelView>> ListAsync(CancellationToken ct)
    {
        var widgets = await db.Widgets.AsNoTracking().Where(w => w.WhatsAppChannelId != null)
            .GroupBy(w => w.WhatsAppChannelId!.Value)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ChannelId, g => g.Count, ct);
        var rows = await (
                from c in db.WhatsAppChannels.AsNoTracking()
                join s in db.Sites on c.SiteId equals s.Id
                orderby c.Name
                select new { Channel = c, SiteCode = s.Code })
            .ToListAsync(ct);
        return rows.Select(r => ToView(r.Channel, r.SiteCode, widgets.GetValueOrDefault(r.Channel.Id))).ToList();
    }

    public async Task<WhatsAppChannelView> GetAsync(Guid channelId, CancellationToken ct) =>
        (await ListAsync(ct)).SingleOrDefault(c => c.Id == channelId)
        ?? throw new DomainException(ErrorCodes.WhatsAppChannelNotFound, "WhatsApp channel not found");

    public async Task<WhatsAppChannelView> CreateAsync(WhatsAppChannelInput input, CancellationToken ct)
    {
        await EnsureSiteAsync(input.SiteId, ct);
        var channel = new WhatsAppChannel(input.Name, input.Mode, input.PhoneNumber, input.SiteId, input.IsEnabled, time.GetUtcNow());
        await EnsurePhoneFreeAsync(channel, ct);
        db.WhatsAppChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return await GetAsync(channel.Id, ct);
    }

    public async Task<WhatsAppChannelView> UpdateAsync(Guid channelId, WhatsAppChannelInput input, CancellationToken ct)
    {
        var channel = await FindAsync(channelId, ct);
        await EnsureSiteAsync(input.SiteId, ct);
        channel.Update(input.Name, input.Mode, input.PhoneNumber, input.SiteId, input.IsEnabled);
        await EnsurePhoneFreeAsync(channel, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(channel.Id, ct);
    }

    /// <summary>Удаляет канал. Кнопка WhatsApp пропадает из виджетов (на сайтах — после публикации).</summary>
    public async Task DeleteAsync(Guid channelId, CancellationToken ct)
    {
        var channel = await FindAsync(channelId, ct);
        var now = time.GetUtcNow();
        foreach (var widget in await db.Widgets.Where(w => w.WhatsAppChannelId == channelId).ToListAsync(ct))
            widget.DetachWhatsAppChannel(now);
        db.WhatsAppChannels.Remove(channel);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Шлюз сообщил, что номер подключён или отключён. false — канала нет (его удалили).</summary>
    public async Task<bool> OnGatewayStatusAsync(Guid channelId, bool connected, string? phone, string? accountName,
        CancellationToken ct)
    {
        var channel = await db.WhatsAppChannels.SingleOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null)
            return false;
        if (connected)
            channel.OnConnected(phone, accountName, time.GetUtcNow());
        else
            channel.OnDisconnected();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task MarkDisconnectedAsync(Guid channelId, CancellationToken ct)
    {
        var channel = await FindAsync(channelId, ct);
        channel.OnDisconnected();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Номер, на который виджет ведёт посетителя, или null, если канал не выбран или выключен.</summary>
    public async Task<string?> GetWidgetPhoneAsync(Widget widget, CancellationToken ct) =>
        (await GetWidgetLinkAsync(widget, ct)).Phone;

    /// <summary>Номер для кнопки WhatsApp и нужен ли код перехода (режим QR: переписка продолжится в Relayo).</summary>
    public async Task<(string? Phone, bool WithCode)> GetWidgetLinkAsync(Widget widget, CancellationToken ct)
    {
        if (widget.WhatsAppChannelId is not { } id)
            return (null, false);
        var channel = await db.WhatsAppChannels.AsNoTracking()
            .Where(c => c.Id == id && c.IsEnabled && c.PhoneNumber != null)
            .Select(c => new { c.PhoneNumber, c.Mode })
            .SingleOrDefaultAsync(ct);
        return channel is null ? (null, false) : (channel.PhoneNumber, channel.Mode == WhatsAppMode.Gateway);
    }

    public async Task<WhatsAppChannel> FindAsync(Guid channelId, CancellationToken ct) =>
        await db.WhatsAppChannels.SingleOrDefaultAsync(c => c.Id == channelId, ct)
        ?? throw new DomainException(ErrorCodes.WhatsAppChannelNotFound, "WhatsApp channel not found");

    private async Task EnsureSiteAsync(Guid siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw new DomainException(ErrorCodes.SiteNotFound, "Site not found");
    }

    /// <summary>Один номер — один канал «только ссылка»: иначе непонятно, какой из них выбирать в виджете.</summary>
    private async Task EnsurePhoneFreeAsync(WhatsAppChannel channel, CancellationToken ct)
    {
        if (channel.Mode != WhatsAppMode.LinkOnly || channel.PhoneNumber is not { } phone)
            return;
        if (await db.WhatsAppChannels.AnyAsync(c => c.PhoneNumber == phone && c.Id != channel.Id, ct))
            throw new DomainException(ErrorCodes.PhoneTaken, $"The number +{phone} is already connected");
    }

    private static WhatsAppChannelView ToView(WhatsAppChannel c, string siteCode, int widgetCount) =>
        new(c.Id, c.Name, c.Mode, c.PhoneNumber, c.SiteId, siteCode, c.IsEnabled, c.IsConnected, c.AccountName, c.ConnectedAt,
            c.CreatedAt, widgetCount);
}
