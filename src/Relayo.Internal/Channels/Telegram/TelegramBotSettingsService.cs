using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Core.Data;
using Relayo.Core.Security;
using Relayo.Internal.Configuration;

namespace Relayo.Internal.Channels.Telegram;

/// <summary>Бот в настройках. Токен наружу не отдаётся: его можно только заменить.</summary>
public sealed record TelegramBotView(
    Guid Id,
    string Name,
    string Username,
    Guid SiteId,
    string SiteCode,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    int WidgetCount);

/// <param name="Token">Токен от @BotFather. При изменении бота пустой токен значит «оставить прежний».</param>
public sealed record TelegramBotInput(string Name, Guid SiteId, bool IsEnabled, string? Token);

/// <summary>Подключение Telegram-ботов (раздел «Настройки», только администратор).</summary>
public sealed class TelegramBotSettingsService(
    RelayoDbContext db,
    SecretProtector protector,
    TelegramBotApi api,
    TelegramBotTokens tokens,
    InternalSettings settings,
    TimeProvider time,
    ILogger<TelegramBotSettingsService> logger)
{
    public async Task<IReadOnlyList<TelegramBotView>> ListAsync(CancellationToken ct)
    {
        var widgets = await db.Widgets.AsNoTracking().Where(w => w.TelegramBotId != null)
            .GroupBy(w => w.TelegramBotId!.Value)
            .Select(g => new { BotId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.BotId, g => g.Count, ct);
        var rows = await (
                from b in db.TelegramBots.AsNoTracking()
                join s in db.Sites on b.SiteId equals s.Id
                orderby b.Name
                select new { Bot = b, SiteCode = s.Code })
            .ToListAsync(ct);
        return rows.Select(r => ToView(r.Bot, r.SiteCode, widgets.GetValueOrDefault(r.Bot.Id))).ToList();
    }

    public async Task<TelegramBotView> CreateAsync(TelegramBotInput input, CancellationToken ct)
    {
        var token = input.Token?.Trim();
        if (string.IsNullOrEmpty(token))
            throw new DomainException(ErrorCodes.InvalidInput, "Bot token is required");
        await EnsureSiteAsync(input.SiteId, ct);
        var me = await GetMeAsync(token, ct);
        if (await db.TelegramBots.AnyAsync(b => b.TelegramId == me.Id, ct))
            throw new DomainException(ErrorCodes.BotTaken, $"Bot @{me.Username} is already connected");

        var bot = new TelegramBot(ValidName(input.Name, me), me.Id, me.Username!, input.SiteId, protector.Protect(token),
            protector.Protect(NewWebhookSecret()), time.GetUtcNow());
        bot.Update(bot.Name, input.SiteId, input.IsEnabled);
        // Webhook регистрируется до сохранения: если Telegram его не примет, бот не появится наполовину настроенным.
        await SyncWebhookAsync(bot, token, ct);
        db.TelegramBots.Add(bot);
        await db.SaveChangesAsync(ct);
        return (await ListAsync(ct)).Single(b => b.Id == bot.Id);
    }

    /// <summary>
    /// Бот из Usedesk. Создаётся выключенным и <b>не трогает вебхук</b>: у бота он один, и пока бот работает в Usedesk,
    /// забрать его нельзя. В день переключения администратор включает бота — вебхук переходит на Relayo.
    /// Telegram только спрашивается, чей это токен (getMe).
    /// </summary>
    public async Task<TelegramBotView> ImportDisabledAsync(string name, Guid siteId, string token, int usedeskChannelId,
        CancellationToken ct)
    {
        token = token.Trim();
        await EnsureSiteAsync(siteId, ct);
        var me = await GetMeAsync(token, ct);
        if (await db.TelegramBots.AnyAsync(b => b.TelegramId == me.Id, ct))
            throw new DomainException(ErrorCodes.BotTaken, $"Bot @{me.Username} is already connected");

        var bot = new TelegramBot(ValidName(name, me), me.Id, me.Username!, siteId, protector.Protect(token),
            protector.Protect(NewWebhookSecret()), time.GetUtcNow());
        bot.Update(bot.Name, siteId, enabled: false);
        bot.LinkUsedeskChannel(usedeskChannelId);
        db.TelegramBots.Add(bot);
        await db.SaveChangesAsync(ct);
        return (await ListAsync(ct)).Single(b => b.Id == bot.Id);
    }

    public async Task<TelegramBotView> UpdateAsync(Guid botId, TelegramBotInput input, CancellationToken ct)
    {
        var bot = await db.TelegramBots.SingleOrDefaultAsync(b => b.Id == botId, ct)
                  ?? throw new DomainException(ErrorCodes.BotNotFound, "Telegram bot not found");
        await EnsureSiteAsync(input.SiteId, ct);

        var token = input.Token?.Trim();
        if (!string.IsNullOrEmpty(token))
        {
            var me = await GetMeAsync(token, ct);
            if (await db.TelegramBots.AnyAsync(b => b.TelegramId == me.Id && b.Id != bot.Id, ct))
                throw new DomainException(ErrorCodes.BotTaken, $"Bot @{me.Username} is already connected");
            bot.ReplaceToken(me.Id, me.Username!, protector.Protect(token));
        }
        else
        {
            token = protector.Unprotect(bot.ProtectedToken);
        }

        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > TelegramBot.NameMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"Name must be 1-{TelegramBot.NameMaxLength} characters");
        bot.Update(name, input.SiteId, input.IsEnabled);

        await SyncWebhookAsync(bot, token, ct);
        await db.SaveChangesAsync(ct);
        tokens.Invalidate(bot.Id);
        return (await ListAsync(ct)).Single(b => b.Id == bot.Id);
    }

    /// <summary>
    /// Удаляет бота. Переписка остаётся в тикетах, но ответы в Telegram через этого бота больше не уходят:
    /// оператор видит их только в виджете. Кнопка Telegram пропадает из виджетов после публикации.
    /// </summary>
    public async Task DeleteAsync(Guid botId, CancellationToken ct)
    {
        var bot = await db.TelegramBots.SingleOrDefaultAsync(b => b.Id == botId, ct)
                  ?? throw new DomainException(ErrorCodes.BotNotFound, "Telegram bot not found");

        if (settings.Telegram.Mode == TelegramUpdatesMode.Webhook)
        {
            try
            {
                await api.DeleteWebhookAsync(protector.Unprotect(bot.ProtectedToken), ct);
            }
            catch (Exception ex) when (ex is TelegramApiException or HttpRequestException or TaskCanceledException)
            {
                // Бот мог быть уже удалён в @BotFather: это не повод оставлять его в Relayo.
                logger.LogWarning("Could not remove the webhook of bot {BotId}: {Error}", bot.Id, ex.Message);
            }
        }

        var now = time.GetUtcNow();
        foreach (var widget in await db.Widgets.Where(w => w.TelegramBotId == botId).ToListAsync(ct))
            widget.DetachTelegramBot(now);
        db.TelegramBots.Remove(bot);
        await db.SaveChangesAsync(ct);
        tokens.Invalidate(bot.Id);
    }

    /// <summary>
    /// В режиме webhook сообщает Telegram, куда присылать обновления включённого бота, и отключает выключенного.
    /// В режиме long polling ничего не делает: обновления забирает <see cref="TelegramUpdatesService"/>.
    /// </summary>
    public async Task SyncWebhookAsync(TelegramBot bot, string token, CancellationToken ct)
    {
        if (settings.Telegram.Mode != TelegramUpdatesMode.Webhook)
            return;
        try
        {
            if (bot.IsEnabled)
            {
                var url = WebhookUrl(settings.PublicBaseUrl, bot.Id);
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new DomainException(ErrorCodes.WebhookRequiresHttps,
                        "Telegram delivers messages only to a public HTTPS address. Set PUBLIC_BASE_URL to https://… "
                        + "or use TELEGRAM_UPDATES_MODE=polling for local development");
                await api.SetWebhookAsync(token, url, protector.Unprotect(bot.ProtectedWebhookSecret), ct);
            }
            else
            {
                await api.DeleteWebhookAsync(token, ct);
            }
        }
        catch (TelegramApiException ex)
        {
            throw new DomainException(ErrorCodes.TelegramUnavailable, $"Telegram did not accept the webhook: {ex.Message}");
        }
        catch (HttpRequestException)
        {
            throw Unavailable();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable();
        }
    }

    public static string WebhookUrl(string publicBaseUrl, Guid botId) =>
        $"{publicBaseUrl.TrimEnd('/')}/telegram/{botId}/webhook";

    private async Task<TgUser> GetMeAsync(string token, CancellationToken ct)
    {
        if (!TelegramBotApi.IsValidToken(token))
            throw new DomainException(ErrorCodes.InvalidBotToken, "This does not look like a bot token from @BotFather");
        try
        {
            var me = await api.GetMeAsync(token, ct);
            if (!me.IsBot || string.IsNullOrEmpty(me.Username))
                throw new DomainException(ErrorCodes.InvalidBotToken, "The token does not belong to a bot");
            return me;
        }
        catch (TelegramApiException ex) when (ex.IsPermanent)
        {
            throw new DomainException(ErrorCodes.InvalidBotToken, "Telegram rejected the token. Check it in @BotFather");
        }
        catch (TelegramApiException)
        {
            throw Unavailable();
        }
        catch (HttpRequestException)
        {
            throw Unavailable();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable();
        }
    }

    private async Task EnsureSiteAsync(Guid siteId, CancellationToken ct)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == siteId, ct))
            throw new DomainException(ErrorCodes.SiteNotFound, "Site not found");
    }

    private static string ValidName(string? name, TgUser me)
    {
        name = string.IsNullOrWhiteSpace(name) ? me.FirstName ?? me.Username! : name.Trim();
        return name.Length > TelegramBot.NameMaxLength ? name[..TelegramBot.NameMaxLength] : name;
    }

    /// <summary>Telegram разрешает в секрете webhook 1-256 символов A-Z, a-z, 0-9, _ и -.</summary>
    internal static string NewWebhookSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static DomainException Unavailable() =>
        new(ErrorCodes.TelegramUnavailable, "Could not reach Telegram. Check the network or TELEGRAM_PROXY_URL");

    private static TelegramBotView ToView(TelegramBot b, string siteCode, int widgetCount) =>
        new(b.Id, b.Name, b.Username, b.SiteId, siteCode, b.IsEnabled, b.CreatedAt, widgetCount);
}
