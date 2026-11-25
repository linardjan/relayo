using System.Text.Json;
using System.Text.RegularExpressions;
using Relayo.Core.Sites;

namespace Relayo.Tests;

public class WidgetPublisherTests
{
    [Fact]
    public void Widget_file_carries_settings_that_survive_special_characters()
    {
        var publisher = new WidgetPublisher(null!, null!, new WidgetPublishingOptions("https://chat.example.com/", "https://chat.example.com/w"), TimeProvider.System);
        var snapshot = new WidgetSnapshot("w1", "Поддержка \"24/7\" </script> \\ ✓", "Привет!\nКак дела?", "#1677ff", "right",
            "signed_token", "ru", 30, "SupportBot");

        var script = publisher.BuildScript("k7Qm2xPa9c", 3, snapshot, DateTimeOffset.UnixEpoch);

        Assert.Contains("s.src = \"https://chat.example.com/widget/core.js\";", script);
        Assert.Contains("'data-relayo-api', \"https://chat.example.com\"", script);
        var literal = Regex.Match(script, @"data-relayo-config', ("".*"")\);").Groups[1].Value;
        var json = JsonSerializer.Deserialize<string>(literal)!;
        var parsed = JsonSerializer.Deserialize<WidgetSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(snapshot, parsed);
    }

    [Fact]
    public void File_names_follow_the_public_key()
    {
        Assert.Equal("widget_k7Qm2xPa9c.js", WidgetPublisher.FileName("k7Qm2xPa9c"));
        Assert.Equal("widget_k7Qm2xPa9c.v12.js", WidgetPublisher.VersionFileName("k7Qm2xPa9c", 12));
        Assert.True(WidgetKeys.IsValid(WidgetKeys.Create()));
        Assert.False(WidgetKeys.IsValid("../etc/pwd"));
    }
}
