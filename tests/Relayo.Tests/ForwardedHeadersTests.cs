using Relayo.ServiceDefaults;

namespace Relayo.Tests;

public class ForwardedHeadersTests
{
    [Fact]
    public void Parses_addresses_and_networks()
    {
        var networks = ForwardedHeaders.Parse(" 10.0.0.5, 172.30.0.0/24 ,::1 ");
        Assert.Equal(["10.0.0.5/32", "172.30.0.0/24", "::1/128"], networks.Select(n => n.ToString()));
    }

    [Fact]
    public void Empty_means_no_proxies() => Assert.Empty(ForwardedHeaders.Parse(null));

    [Fact]
    public void Garbage_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => ForwardedHeaders.Parse("proxy.local"));
}
