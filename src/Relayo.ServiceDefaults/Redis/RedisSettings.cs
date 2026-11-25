using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Relayo.ServiceDefaults.Redis;

/// <summary>
/// Подключение к Redis (REDIS_*). Redis нужен, когда сервис работает в нескольких экземплярах:
/// общие лимиты запросов, SignalR backplane, блокировки. Без REDIS_HOST сервис работает как один экземпляр.
/// </summary>
public sealed record RedisSettings(string Host, int Port, string? Password, bool Ssl)
{
    /// <summary>Префикс всех ключей и каналов Relayo: в одном Redis могут жить и другие приложения.</summary>
    public const string KeyPrefix = "relayo:";

    public static RedisSettings? Load(RelayoConfig config)
    {
        var host = config.Optional("REDIS_HOST");
        return host is null
            ? null
            : new RedisSettings(host, config.Int("REDIS_PORT", 6379), config.Optional("REDIS_PASSWORD"), config.Bool("REDIS_SSL", false));
    }

    public ConfigurationOptions ToOptions(string clientName)
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { { Host, Port } },
            Password = Password,
            Ssl = Ssl,
            ClientName = clientName,
            // Redis может подняться позже сервиса или ненадолго пропасть: подключаемся в фоне, а не падаем.
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000,
        };
        return options;
    }
}

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует подключение к Redis и проверку в /health/ready. Без настроек ничего не делает:
    /// компоненты, которым Redis нужен, тогда работают в пределах одного экземпляра.
    /// </summary>
    public static IServiceCollection AddRelayoRedis(this IServiceCollection services, RedisSettings? settings, string clientName)
    {
        if (settings is null)
            return services;
        services.AddSingleton(settings);
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.ToOptions(clientName)));
        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: [ServiceDefaultsExtensions.ReadyTag]);
        return services;
    }
}

/// <summary>
/// Redis отвечает на PING. Без него экземпляры перестают делить лимиты и события, но работать продолжают,
/// поэтому статус — Degraded (готовность остаётся 200): иначе сбой Redis снял бы с балансировщика все экземпляры.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!redis.IsConnected)
            return HealthCheckResult.Degraded("Redis is unavailable: no connection");
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"PING {latency.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Degraded("Redis is unavailable", ex);
        }
    }
}
