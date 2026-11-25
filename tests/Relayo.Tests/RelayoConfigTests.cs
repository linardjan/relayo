using Microsoft.Extensions.Configuration;
using Relayo.ServiceDefaults;

namespace Relayo.Tests;

public class RelayoConfigTests
{
    private static RelayoConfig Config(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build());

    [Fact]
    public void Reports_all_problems_at_once()
    {
        var config = Config(("PORT", "abc"), ("KEY", Convert.ToBase64String(new byte[8])));

        config.Required("MISSING_A");
        config.Required("MISSING_B");
        config.Int("PORT", 1);
        config.Base64Key("KEY", minBytes: 32);

        var ex = Assert.Throws<InvalidOperationException>(config.ThrowIfInvalid);
        Assert.Contains("MISSING_A", ex.Message);
        Assert.Contains("MISSING_B", ex.Message);
        Assert.Contains("PORT must be an integer", ex.Message);
        Assert.Contains("KEY must be at least 32 bytes", ex.Message);
    }

    [Fact]
    public void Blank_values_count_as_missing_and_defaults_apply()
    {
        var config = Config(("BLANK", "   "), ("LIST", "a, b,,c"));

        Assert.Null(config.Optional("BLANK"));
        Assert.Equal(42, config.Int("ABSENT", 42));
        Assert.True(config.Bool("ABSENT", true));
        Assert.Equal(["a", "b", "c"], config.List("LIST"));
        config.ThrowIfInvalid();
    }
}
