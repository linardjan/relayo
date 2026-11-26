using Relayo.Core;
using Relayo.Core.Sites;

namespace Relayo.Tests;

public class WidgetOptionsTests
{
    [Fact]
    public void Chat_item_is_always_present_and_telegram_follows_the_bot()
    {
        var withBot = WidgetOptions.Default.Normalize(hasTelegramBot: true);
        Assert.Equal([LauncherItemType.Chat, LauncherItemType.Telegram], withBot.Items.Select(i => i.Type));

        var noChat = (WidgetOptions.Default with { Items = [new(LauncherItemType.Telegram, "TG")] }).Normalize(hasTelegramBot: false);
        Assert.Equal([LauncherItemType.Chat], noChat.Items.Select(i => i.Type));
    }

    [Fact]
    public void Whatsapp_item_follows_the_channel()
    {
        var withChannel = WidgetOptions.Default.Normalize(hasTelegramBot: false, hasWhatsApp: true);
        Assert.Equal(new LauncherItem(LauncherItemType.WhatsApp, "WhatsApp"), withChannel.Items[^1]);

        var noChannel = (WidgetOptions.Default with { Items = [new(LauncherItemType.WhatsApp, "WA")] }).Normalize(false, false);
        Assert.Equal([LauncherItemType.Chat], noChannel.Items.Select(i => i.Type));

        Assert.Throws<DomainException>(() => (WidgetOptions.Default with
        {
            Items = [new(LauncherItemType.WhatsApp, "a"), new(LauncherItemType.WhatsApp, "b")],
        }).Normalize(false, true));
    }

    [Fact]
    public void Items_keep_order_and_get_default_labels()
    {
        var options = (WidgetOptions.Default with
        {
            Items = [new(LauncherItemType.Link, " Help ", "https://example.com/help"), new(LauncherItemType.Chat, "")],
        }).Normalize(hasTelegramBot: false);
        Assert.Equal(new LauncherItem(LauncherItemType.Link, "Help", "https://example.com/help"), options.Items[0]);
        Assert.Equal("Online chat", options.Items[1].Label);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData(null)]
    public void Link_needs_http_address(string? url) =>
        Assert.Throws<DomainException>(() =>
            (WidgetOptions.Default with { Items = [new(LauncherItemType.Link, "x", url)] }).Normalize(false));

    [Fact]
    public void Duplicate_chat_item_is_rejected() =>
        Assert.Throws<DomainException>(() =>
            (WidgetOptions.Default with { Items = [new(LauncherItemType.Chat, "a"), new(LauncherItemType.Chat, "b")] }).Normalize(false));

    [Fact]
    public void Colors_and_ranges_are_checked()
    {
        Assert.Throws<DomainException>(() => (WidgetOptions.Default with { ButtonColor = "red" }).Normalize(false));
        Assert.Throws<DomainException>(() => (WidgetOptions.Default with { TooltipTextSize = 200 }).Normalize(false));
        Assert.Throws<DomainException>(() => (WidgetOptions.Default with { ButtonDelaySeconds = -1 }).Normalize(false));
        Assert.Equal("#aabbcc", (WidgetOptions.Default with { ButtonColor = "#AABBCC" }).Normalize(false).ButtonColor);
        Assert.Null((WidgetOptions.Default with { ButtonColor = " " }).Normalize(false).ButtonColor);
    }
}
