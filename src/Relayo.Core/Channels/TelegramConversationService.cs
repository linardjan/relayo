using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Relayo.Core.Clients;
using Relayo.Core.Conversations;
using Relayo.Core.Data;
using Relayo.Core.Sites;

namespace Relayo.Core.Channels;

/// <summary>Отправитель сообщения в Telegram: личный чат человека с ботом.</summary>
public sealed record TelegramSender(long ChatId, long UserId, string? FirstName, string? LastName, string? Username,
    string? LanguageCode)
{
    public string DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { FirstName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (Username is { Length: > 0 })
                name = name.Length > 0 ? $"{name} (@{Username})" : $"@{Username}";
            return name.Length > 0 ? name : $"Telegram {UserId}";
        }
    }
}

public sealed record MessengerLink(string Url, DateTimeOffset ExpiresAt);

public enum TelegramStartResult
{
    /// <summary>/start без ссылки из виджета: человек открыл бота сам.</summary>
    Greeting,

    /// <summary>Аккаунт привязан к посетителю виджета, переписка продолжается в Telegram.</summary>
    Linked,

    /// <summary>Ссылка просрочена, уже использована или не от этого бота.</summary>
    LinkExpired,
}

/// <summary>Что произошло при обработке /start: ответ бота и, если появилось, системное сообщение в чате.</summary>
public sealed record TelegramStart(TelegramStartResult Result, PostedVisitorMessage? SystemMessage);

/// <summary>
/// Переписка через Telegram: ссылки для перехода из виджета, привязка аккаунта и сообщения клиента
/// (п. 3.3 ТЗ, шаги 5 и 7). Вызовы Bot API здесь нет: этим занимается внутренний сервис.
/// </summary>
public sealed class TelegramConversationService(
    RelayoDbContext db,
    WidgetConversationService conversations,
    TimeProvider time)
{
    public const string StartCommand = "/start";

    public Task<TelegramBot?> FindBotAsync(Guid botId, CancellationToken ct) =>
        db.TelegramBots.AsNoTracking().SingleOrDefaultAsync(b => b.Id == botId, ct);

    public async Task<string?> GetWidgetBotUsernameAsync(Widget widget, CancellationToken ct) =>
        widget.TelegramBotId is { } botId
            ? await db.TelegramBots.Where(b => b.Id == botId && b.IsEnabled).Select(b => b.Username)
                .SingleOrDefaultAsync(ct)
            : null;

    /// <summary>
    /// Одноразовая ссылка t.me/&lt;bot&gt;?start=&lt;token&gt;. Виджет запрашивает её заранее и обновляет в фоне,
    /// потому что браузеры блокируют переход, если перед ним по клику шёл сетевой запрос (п. 3.5 ТЗ).
    /// </summary>
    public async Task<MessengerLink> CreateLinkAsync(Widget widget, string visitorToken, CancellationToken ct)
    {
        var bot = widget.TelegramBotId is { } botId
            ? await db.TelegramBots.AsNoTracking().SingleOrDefaultAsync(b => b.Id == botId && b.IsEnabled, ct)
            : null;
        if (bot is null)
            throw new DomainException(ErrorCodes.TelegramNotConnected, "Telegram is not connected to this widget");

        var visitor = await conversations.FindVisitorAsync(widget, visitorToken, ct)
                      ?? throw new DomainException(ErrorCodes.VisitorNotFound, "Visitor not found");

        var now = time.GetUtcNow();
        // Просроченные ссылки больше не нужны: заодно убираем их.
        await db.ChannelLinkTokens.Where(t => t.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);

        // 24 байта в base64url — 32 символа: Telegram разрешает в start до 64 символов A-Z, a-z, 0-9, _ и -.
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var link = new ChannelLinkToken(VisitorTokens.Hash(token), bot.Id, visitor.Id, now);
        db.ChannelLinkTokens.Add(link);
        await db.SaveChangesAsync(ct);

        return new MessengerLink($"https://t.me/{bot.Username}?start={token}", link.ExpiresAt);
    }

    /// <summary>
    /// /start &lt;token&gt;: привязывает аккаунт Telegram к посетителю виджета. Если человек раньше писал боту
    /// напрямую и для него был заведён отдельный анонимный клиент, его переписка переходит к клиенту посетителя.
    /// </summary>
    public async Task<TelegramStart> StartAsync(TelegramBot bot, TelegramSender sender, string? payload,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (string.IsNullOrWhiteSpace(payload))
        {
            await TouchProfileAsync(bot, sender, now, ct);
            return new TelegramStart(TelegramStartResult.Greeting, null);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var hash = VisitorTokens.Hash(payload.Trim());
        var link = await db.ChannelLinkTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.BotId == bot.Id, ct);
        var visitor = link is null
            ? null
            : await db.ClientIdentities.Include(i => i.Client)
                .SingleOrDefaultAsync(i => i.Id == link.VisitorIdentityId && i.Type == IdentityType.Visitor, ct);
        if (link is null || visitor is null || link.IsExpired(now))
            return new TelegramStart(TelegramStartResult.LinkExpired, null);

        var identity = await FindIdentityAsync(bot, sender.ChatId, ct);
        if (link.UsedAt is not null)
        {
            // Повторный /start по той же ссылке (нажали дважды, Telegram повторил запрос) — не ошибка.
            return identity?.LinkedFromIdentityId == visitor.Id
                ? new TelegramStart(TelegramStartResult.Linked, null)
                : new TelegramStart(TelegramStartResult.LinkExpired, null);
        }
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

        if (identity is null)
        {
            identity = ClientIdentity.CreateTelegram(bot.Id, sender.ChatId, visitor.SiteId, now);
            db.ClientIdentities.Add(identity);
        }
        else if (identity.Client is { } previous && previous.Id != target.Id && previous.IsAnonymous)
        {
            // Человек уже писал боту напрямую: его отдельный анонимный клиент объединяется с клиентом посетителя.
            await conversations.LockClientAsync(previous.Id, ct);
            await conversations.MergeAnonymousAsync(previous, target, identity.Id, now, ct);
        }
        // Если аккаунт был привязан к другому опознанному клиенту, его старые тикеты остаются у того клиента,
        // а новые сообщения пойдут клиенту посетителя: последняя привязка побеждает.

        identity.LinkFrom(visitor, target);
        identity.SetDisplayName(sender.DisplayName);
        identity.Touch(now);
        target.SetDisplayNameIfEmpty(sender.DisplayName);
        target.SetLocaleIfEmpty(sender.LanguageCode);
        await db.SaveChangesAsync(ct);

        // Оператор и посетитель видят в чате, что переписка продолжилась в Telegram.
        PostedVisitorMessage? system = null;
        if (await FindVisitorChatAsync(target.Id, visitor.Id, ct) is { } chat)
        {
            var message = Message.System(chat, identity.Id, Channel.Telegram, "The client continued the chat in Telegram", now);
            db.Messages.Add(message);
            chat.OnMessage(now);
            await db.SaveChangesAsync(ct);
            system = new PostedVisitorMessage(WidgetConversationService.ToView(message, null), chat.TicketId, false,
                await conversations.GetRecipientsAsync(chat, ct));
        }

        await tx.CommitAsync(ct);
        return new TelegramStart(TelegramStartResult.Linked, system);
    }

    /// <summary>Сообщение человека боту. Если раньше он боту не писал, заводится новый анонимный клиент (п. 3.3, шаг 7).</summary>
    public async Task<PostedVisitorMessage> ReceiveAsync(TelegramBot bot, TelegramSender sender, long messageId,
        string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length > Message.TextMaxLength)
            text = text[..Message.TextMaxLength];
        if (text.Length == 0)
            throw new DomainException(ErrorCodes.InvalidInput, "Message is empty");

        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var identity = await GetOrCreateIdentityAsync(bot, sender, now, ct);
        var posted = await conversations.PostAsync(identity, Channel.Telegram, text, $"tg:{messageId}", now, ct);
        identity.Client?.SetDisplayNameIfEmpty(sender.DisplayName);
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return posted;
    }

    private Task<ClientIdentity?> FindIdentityAsync(TelegramBot bot, long chatId, CancellationToken ct)
    {
        var externalId = ClientIdentity.TelegramExternalId(bot.Id, chatId);
        return db.ClientIdentities.Include(i => i.Client)
            .SingleOrDefaultAsync(i => i.Type == IdentityType.Telegram && i.ExternalId == externalId, ct);
    }

    private async Task<ClientIdentity> GetOrCreateIdentityAsync(TelegramBot bot, TelegramSender sender,
        DateTimeOffset now, CancellationToken ct)
    {
        var identity = await FindIdentityAsync(bot, sender.ChatId, ct);
        if (identity is null)
        {
            // Два первых сообщения подряд не должны создать двух клиентов.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"tg:" + bot.Id + ":" + sender.ChatId}, 0))", ct);
            identity = await FindIdentityAsync(bot, sender.ChatId, ct);
        }
        if (identity is null)
        {
            identity = ClientIdentity.CreateTelegram(bot.Id, sender.ChatId, bot.SiteId, now);
            db.ClientIdentities.Add(identity);
        }

        if (identity.Client is null)
        {
            var client = Client.CreateAnonymous(identity.SiteId, now);
            client.SetDisplayNameIfEmpty(sender.DisplayName);
            client.SetLocale(sender.LanguageCode);
            db.Clients.Add(client);
            identity.AttachTo(client, verified: false);
        }
        identity.SetDisplayName(sender.DisplayName);
        await db.SaveChangesAsync(ct);
        return identity;
    }

    private async Task TouchProfileAsync(TelegramBot bot, TelegramSender sender, DateTimeOffset now,
        CancellationToken ct)
    {
        if (await FindIdentityAsync(bot, sender.ChatId, ct) is not { } identity)
            return;
        identity.SetDisplayName(sender.DisplayName);
        identity.Touch(now);
        await db.SaveChangesAsync(ct);
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
}
