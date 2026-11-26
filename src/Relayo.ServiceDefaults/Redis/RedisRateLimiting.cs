using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Relayo.ServiceDefaults.Redis;

/// <summary>
/// Лимиты «N запросов в окно» для нескольких экземпляров: счётчик окна лежит в Redis, поэтому лимит общий.
/// Без Redis — обычный лимитер в памяти экземпляра.
/// </summary>
public static class RelayoRateLimits
{
    /// <param name="redis">null — Redis не настроен, лимит в памяти.</param>
    public static RateLimitPartition<string> FixedWindow(IConnectionMultiplexer? redis, ILogger logger, string key,
        int permitLimit, TimeSpan window) =>
        redis is null
            ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
            })
            : RateLimitPartition.Get(key, k => new RedisFixedWindowRateLimiter(redis, logger, k, permitLimit, window));
}

/// <summary>
/// Фиксированное окно в Redis: INCR счётчика текущего окна, у нового счётчика — срок жизни окна.
/// Если Redis недоступен, запрос пропускается (лимит — защита, а не причина отказать всем).
/// </summary>
public sealed class RedisFixedWindowRateLimiter(
    IConnectionMultiplexer redis,
    ILogger logger,
    string key,
    int permitLimit,
    TimeSpan window) : RateLimiter
{
    // Атомарно: увеличить счётчик и, если он новый, задать срок жизни.
    private const string Script = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        return count
        """;

    private static long lastWarningTicks;
    private long lastUsedTicks = DateTime.UtcNow.Ticks;

    public override TimeSpan? IdleDuration => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastUsedTicks));

    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>
    /// Синхронно Redis не спрашиваем: middleware в этом случае вызывает <see cref="AcquireAsyncCore"/>.
    /// </summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Lease.Rejected(null);

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct)
    {
        Interlocked.Exchange(ref lastUsedTicks, DateTime.UtcNow.Ticks);
        var windowMs = (long)window.TotalMilliseconds;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowIndex = nowMs / windowMs;
        var redisKey = $"{RedisSettings.KeyPrefix}rl:{key}:{windowIndex}";
        // Соединения нет — не ждём таймаута на каждом запросе, сразу пропускаем.
        if (!redis.IsConnected)
        {
            WarnUnavailable("no connection");
            return Lease.Acquired;
        }
        try
        {
            var count = (long)await redis.GetDatabase().ScriptEvaluateAsync(Script, [redisKey], [windowMs + 1000]);
            if (count <= permitLimit)
                return Lease.Acquired;
            return Lease.Rejected(TimeSpan.FromMilliseconds((windowIndex + 1) * windowMs - nowMs));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            WarnUnavailable(ex.GetType().Name);
            return Lease.Acquired;
        }
    }

    /// <summary>Не чаще раза в минуту, чтобы сбой Redis не заваливал логи.</summary>
    private void WarnUnavailable(string reason)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastWarningTicks);
        if (now - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref lastWarningTicks, now, last) == last)
            logger.LogWarning("Rate limits are local to this instance: Redis is unavailable ({Reason})", reason);
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public static readonly Lease Acquired = new(true, null);

        public static Lease Rejected(TimeSpan? retryAfter) => new(false, retryAfter);

        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}
