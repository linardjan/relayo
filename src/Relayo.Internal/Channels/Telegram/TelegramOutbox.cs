using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;
using Relayo.Core.Files;
using Relayo.Core.Security;
using Relayo.Core.Sites;
using Relayo.ServiceDefaults.Redis;
using StackExchange.Redis;
using Wolverine;

namespace Relayo.Internal.Channels.Telegram;

/// <summary>Файл для отправки в Telegram: лежит в S3, в очереди — только ссылка на него.</summary>
public sealed record TelegramFile(string StorageKey, string FileName, string ContentType);

/// <summary>
/// Сообщение для отправки в Telegram. Идёт через durable-очередь: переживает перезапуск сервиса.
/// Текст (если есть) уходит первым, затем файлы по порядку. Если текста нет, подпись получает первый файл.
/// </summary>
public sealed record SendTelegramMessage(Guid BotId, long ChatId, string Html, IReadOnlyList<TelegramFile>? Files = null,
    string? FirstCaptionHtml = null);

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

    /// <summary>
    /// Ответ оператора: псевдоним жирным, затем текст и файлы. ФИО и логин в Telegram не уходят.
    /// Файлы едут с последней частью текста, чтобы прийти после него.
    /// </summary>
    public async Task SendOperatorReplyAsync(Guid botId, long chatId, string alias, string text,
        IReadOnlyList<StoredFile>? files = null)
    {
        var telegramFiles = files?.Select(f => new TelegramFile(f.StorageKey, f.FileName, f.ContentType)).ToList() ?? [];
        var chunks = Chunk(text).ToList();
        if (chunks.Count == 0)
        {
            if (telegramFiles.Count > 0)
                await bus.PublishAsync(new SendTelegramMessage(botId, chatId, string.Empty, telegramFiles,
                    $"<b>{Escape(alias)}</b>"));
            return;
        }
        for (var i = 0; i < chunks.Count; i++)
        {
            var html = i == 0 ? $"<b>{Escape(alias)}</b>\n{Escape(chunks[i])}" : Escape(chunks[i]);
            var last = i == chunks.Count - 1;
            await bus.PublishAsync(new SendTelegramMessage(botId, chatId, html, last && telegramFiles.Count > 0 ? telegramFiles : null));
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
/// Сверх лимита сообщение ждёт своей очереди, а не теряется. С Redis лимит общий для всех экземпляров
/// внутреннего сервиса, без Redis — в пределах экземпляра. Если Telegram всё же ответит 429,
/// отправка повторится через указанное им время.
/// </summary>
public sealed class TelegramRateLimiter(IConnectionMultiplexer? redis = null, ILogger<TelegramRateLimiter>? logger = null)
    : IDisposable
{
    private const int BotPerSecond = 30;
    private const int ChatPerSecond = 1;

    // Оба счётчика секунды увеличиваются, только если оба ниже лимита; иначе — сколько ждать до следующей секунды.
    private const string Script = """
        local chat = tonumber(redis.call('GET', KEYS[1]) or '0')
        local bot = tonumber(redis.call('GET', KEYS[2]) or '0')
        if chat >= tonumber(ARGV[1]) or bot >= tonumber(ARGV[2]) then return 0 end
        redis.call('INCR', KEYS[1]); redis.call('PEXPIRE', KEYS[1], 2000)
        redis.call('INCR', KEYS[2]); redis.call('PEXPIRE', KEYS[2], 2000)
        return 1
        """;

    private readonly PartitionedRateLimiter<Guid> bots = PartitionedRateLimiter.Create<Guid, Guid>(botId =>
        RateLimitPartition.GetTokenBucketLimiter(botId, _ => Bucket(tokensPerSecond: BotPerSecond)));

    private readonly PartitionedRateLimiter<(Guid, long)> chats = PartitionedRateLimiter.Create<(Guid, long), (Guid, long)>(
        chat => RateLimitPartition.GetTokenBucketLimiter(chat, _ => Bucket(tokensPerSecond: ChatPerSecond)));

    public async Task<IDisposable> AcquireAsync(Guid botId, long chatId, CancellationToken ct)
    {
        if (redis is { IsConnected: true } && await TryAcquireSharedAsync(botId, chatId, ct))
            return NoLease.Instance;

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

    /// <summary>Общий лимит в Redis. false — Redis недоступен, действует лимит экземпляра.</summary>
    private async Task<bool> TryAcquireSharedAsync(Guid botId, long chatId, CancellationToken ct)
    {
        try
        {
            var db = redis!.GetDatabase();
            while (true)
            {
                var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                RedisKey[] keys =
                [
                    $"{RedisSettings.KeyPrefix}tg:chat:{botId:N}:{chatId}:{second}",
                    $"{RedisSettings.KeyPrefix}tg:bot:{botId:N}:{second}",
                ];
                if ((long)await db.ScriptEvaluateAsync(Script, keys, [ChatPerSecond, BotPerSecond]) == 1)
                    return true;
                // Ждём начала следующей секунды.
                var wait = 1000 - (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000);
                await Task.Delay(wait + Random.Shared.Next(0, 30), ct);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger?.LogWarning("Telegram rate limit is local to this instance: Redis is unavailable ({Error})", ex.Message);
            return false;
        }
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

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Leases(RateLimitLease first, RateLimitLease second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }
}

/// <summary>
/// Расшифрованные токены ботов. Держатся в памяти минуту, чтобы не ходить в БД на каждое сообщение.
/// Когда бота меняют или удаляют, кэш сбрасывается на всех экземплярах (канал Redis), без Redis — на этом.
/// </summary>
public sealed class TelegramBotTokens
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private static readonly RedisChannel Changes = RedisChannel.Literal($"{RedisSettings.KeyPrefix}telegram-bot-changed");
    private readonly ConcurrentDictionary<Guid, (string? Token, DateTimeOffset LoadedAt)> cache = new();
    private readonly IServiceScopeFactory scopes;
    private readonly SecretProtector protector;
    private readonly TimeProvider time;
    private readonly IConnectionMultiplexer? redis;

    public TelegramBotTokens(IServiceScopeFactory scopes, SecretProtector protector, TimeProvider time,
        IConnectionMultiplexer? redis = null)
    {
        this.scopes = scopes;
        this.protector = protector;
        this.time = time;
        this.redis = redis;
        redis?.GetSubscriber().Subscribe(Changes, (channel, value) =>
        {
            if (Guid.TryParse((string?)value, out var botId))
                cache.TryRemove(botId, out _);
        });
    }

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

    public void Invalidate(Guid botId)
    {
        cache.TryRemove(botId, out _);
        try
        {
            redis?.GetSubscriber().Publish(Changes, botId.ToString(), CommandFlags.FireAndForget);
        }
        catch (RedisException)
        {
            // другие экземпляры обновят кэш сами в течение минуты
        }
    }
}

/// <summary>Обработчик очереди исходящих сообщений Telegram (Wolverine).</summary>
public sealed class SendTelegramMessageHandler(
    TelegramBotTokens tokens,
    TelegramRateLimiter limiter,
    TelegramBotApi api,
    IFileStorage storage,
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

        if (!string.IsNullOrEmpty(message.Html))
            await SendAsync(message, () => api.SendMessageAsync(token, message.ChatId, message.Html, ct), ct);

        var caption = message.FirstCaptionHtml;
        foreach (var file in message.Files ?? [])
        {
            var content = await storage.ReadAttachmentAsync(file.StorageKey, ct);
            var fileCaption = caption;
            await SendAsync(message, () => api.SendFileAsync(token, message.ChatId, content, file.FileName,
                file.ContentType, fileCaption, ct), ct);
            caption = null;
        }
    }

    private async Task SendAsync(SendTelegramMessage message, Func<Task<TgMessage>> send, CancellationToken ct)
    {
        using var lease = await limiter.AcquireAsync(message.BotId, message.ChatId, ct);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await send();
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
