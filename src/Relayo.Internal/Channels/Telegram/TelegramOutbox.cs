using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;
using Relayo.Core.Security;
using Wolverine;

namespace Relayo.Internal.Channels.Telegram;

/// <summary>Сообщение для отправки в Telegram. Идёт через durable-очередь: переживает перезапуск сервиса.</summary>
public sealed record SendTelegramMessage(Guid BotId, long ChatId, string Html);

/// <summary>Исходящие сообщения в Telegram: очередь с лимитом на каждого бота и каждый чат (п. 3.4 ТЗ).</summary>
public sealed class TelegramOutbox(IMessageBus bus)
{
    public const string QueueName = "telegram-outgoing";

    /// <summary>Telegram принимает до 4096 символов: длинный ответ уходит несколькими сообщениями.</summary>
    private const int ChunkLength = 3500;

    public async Task SendTextAsync(Guid botId, long chatId, string text)
    {
        await bus.PublishAsync(new SendTelegramMessage(botId, chatId, Escape(text)));
    }

    /// <summary>Ответ оператора: псевдоним жирным, затем текст. ФИО и логин в Telegram не уходят.</summary>
    public async Task SendOperatorReplyAsync(Guid botId, long chatId, string alias, string text)
    {
        var first = true;
        foreach (var chunk in Chunk(text))
        {
            var html = first ? $"<b>{Escape(alias)}</b>\n{Escape(chunk)}" : Escape(chunk);
            await bus.PublishAsync(new SendTelegramMessage(botId, chatId, html));
            first = false;
        }
    }

    internal static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static IEnumerable<string> Chunk(string text)
    {
        for (var i = 0; i < text.Length; i += ChunkLength)
            yield return text.Substring(i, Math.Min(ChunkLength, text.Length - i));
    }
}

/// <summary>
/// Ограничения Telegram: около 30 сообщений в секунду на бота и 1 в секунду в один чат.
/// Сверх лимита сообщение ждёт своей очереди, а не теряется. Ограничение действует в пределах экземпляра сервиса;
/// если Telegram всё же ответит 429, отправка повторится через указанное им время.
/// </summary>
public sealed class TelegramRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> bots = PartitionedRateLimiter.Create<Guid, Guid>(botId =>
        RateLimitPartition.GetTokenBucketLimiter(botId, _ => Bucket(tokensPerSecond: 30)));

    private readonly PartitionedRateLimiter<(Guid, long)> chats = PartitionedRateLimiter.Create<(Guid, long), (Guid, long)>(
        chat => RateLimitPartition.GetTokenBucketLimiter(chat, _ => Bucket(tokensPerSecond: 1)));

    public async Task<IDisposable> AcquireAsync(Guid botId, long chatId, CancellationToken ct)
    {
        var chat = await chats.AcquireAsync((botId, chatId), 1, ct);
        var bot = await bots.AcquireAsync(botId, 1, ct);
        if (!chat.IsAcquired || !bot.IsAcquired)
        {
            chat.Dispose();
            bot.Dispose();
            throw new InvalidOperationException("Telegram send queue is full");
        }
        return new Leases(chat, bot);
    }

    public void Dispose()
    {
        bots.Dispose();
        chats.Dispose();
    }

    private static TokenBucketRateLimiterOptions Bucket(int tokensPerSecond) => new()
    {
        TokenLimit = tokensPerSecond,
        TokensPerPeriod = tokensPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 10_000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };

    private sealed class Leases(RateLimitLease first, RateLimitLease second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }
}

/// <summary>Расшифрованные токены ботов. Держатся в памяти минуту, чтобы не ходить в БД на каждое сообщение.</summary>
public sealed class TelegramBotTokens(IServiceScopeFactory scopes, SecretProtector protector, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, (string? Token, DateTimeOffset LoadedAt)> cache = new();

    /// <summary>Токен включённого бота или null, если бот удалён или выключен.</summary>
    public async Task<string?> GetAsync(Guid botId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (cache.TryGetValue(botId, out var cached) && now - cached.LoadedAt < CacheFor)
            return cached.Token;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayoDbContext>();
        var bot = await db.TelegramBots.AsNoTracking()
            .Where(b => b.Id == botId && b.IsEnabled)
            .Select(b => new { b.ProtectedToken })
            .SingleOrDefaultAsync(ct);
        var token = bot is null ? null : protector.Unprotect(bot.ProtectedToken);
        cache[botId] = (token, now);
        return token;
    }

    public void Invalidate(Guid botId) => cache.TryRemove(botId, out _);
}

/// <summary>Обработчик очереди исходящих сообщений Telegram (Wolverine).</summary>
public sealed class SendTelegramMessageHandler(
    TelegramBotTokens tokens,
    TelegramRateLimiter limiter,
    TelegramBotApi api,
    ILogger<SendTelegramMessageHandler> logger)
{
    private const int MaxRateLimitRetries = 3;

    public async Task Handle(SendTelegramMessage message, CancellationToken ct)
    {
        var token = await tokens.GetAsync(message.BotId, ct);
        if (token is null)
        {
            logger.LogWarning("Telegram bot {BotId} is disabled or removed: message to chat {ChatId} is dropped",
                message.BotId, message.ChatId);
            return;
        }

        using var lease = await limiter.AcquireAsync(message.BotId, message.ChatId, ct);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await api.SendMessageAsync(token, message.ChatId, message.Html, ct);
                return;
            }
            catch (TelegramApiException ex) when (ex.RetryAfterSeconds is { } wait && attempt < MaxRateLimitRetries)
            {
                logger.LogWarning("Telegram rate limit for bot {BotId}: retrying in {Seconds}s", message.BotId, wait);
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
            catch (TelegramApiException ex) when (ex.IsPermanent)
            {
                // Например, человек заблокировал бота. Повтор не поможет.
                logger.LogWarning("Telegram rejected a message to chat {ChatId} of bot {BotId}: {Error}",
                    message.ChatId, message.BotId, ex.Message);
                return;
            }
        }
    }
}
