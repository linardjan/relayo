using Microsoft.Extensions.Configuration;
using Relayo.ServiceDefaults;
using Relayo.ServiceDefaults.Redis;

namespace Relayo.Tests;

public class RedisSettingsTests
{
    private static RelayoConfig Config(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void Without_host_the_service_runs_as_a_single_instance() =>
        Assert.Null(RedisSettings.Load(Config(("REDIS_HOST", " "), ("REDIS_PASSWORD", "x"))));

    [Fact]
    public void Connection_does_not_fail_the_service_when_redis_is_down()
    {
        var settings = RedisSettings.Load(Config(("REDIS_HOST", "redis.local"), ("REDIS_PORT", "6380"), ("REDIS_PASSWORD", "secret"),
            ("REDIS_SSL", "true")))!;
        var options = settings.ToOptions("relayo-test");
        var endpoint = Assert.IsType<System.Net.DnsEndPoint>(options.EndPoints.Single());
        Assert.Equal(("redis.local", 6380), (endpoint.Host, endpoint.Port));
        Assert.True(options.Ssl);
        Assert.Equal("secret", options.Password);
        Assert.False(options.AbortOnConnectFail);
    }
}
