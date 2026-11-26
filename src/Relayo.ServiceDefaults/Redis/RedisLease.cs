using StackExchange.Redis;

namespace Relayo.ServiceDefaults.Redis;

/// <summary>
/// Аренда в Redis: задачу, которая должна идти в одном экземпляре (например, опрос Telegram), выполняет тот,
/// кто держит аренду. Держатель продлевает её; если экземпляр пропал, аренда истекает и её берёт другой.
/// </summary>
public sealed class RedisLease(IConnectionMultiplexer redis, string name, TimeSpan ttl)
{
    // Продлить или снять аренду может только её держатель.
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
        return 0
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private readonly RedisKey key = $"{RedisSettings.KeyPrefix}lease:{name}";

    /// <summary>Кто держит аренду: имя машины и случайная часть (у экземпляров на одной машине они разные).</summary>
    public string Owner { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public TimeSpan Ttl => ttl;

    /// <summary>Взять свободную аренду или продлить свою. false — аренда у другого экземпляра (или Redis недоступен).</summary>
    public async Task<bool> TryAcquireOrRenewAsync()
    {
        var db = redis.GetDatabase();
        if (await db.StringSetAsync(key, Owner, ttl, When.NotExists))
            return true;
        return (long)await db.ScriptEvaluateAsync(RenewScript, [key], [Owner, (long)ttl.TotalMilliseconds]) == 1;
    }

    public async Task ReleaseAsync()
    {
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [key], [Owner]);
        }
        catch (RedisException)
        {
            // аренда истечёт сама
        }
    }
}
