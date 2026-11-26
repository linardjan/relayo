using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Core.Clients;
using Relayo.Core.Files;

namespace Relayo.Tests;

public class WhatsAppChannelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData("+7 (900) 123-45-67", "79001234567")]
    [InlineData("79001234567", "79001234567")]
    [InlineData(" +44 20 7946 0958 ", "442079460958")]
    public void Phone_is_reduced_to_digits(string input, string expected) =>
        Assert.Equal(expected, WhatsAppChannel.NormalizePhone(input));

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("0123456789")]
    [InlineData("1234567890123456")]
    [InlineData("+7 900 abc")]
    [InlineData("7900;1234567")]
    public void Bad_phone_is_rejected(string input) =>
        Assert.Throws<DomainException>(() => WhatsAppChannel.NormalizePhone(input));

    [Fact]
    public void Name_is_required() =>
        Assert.Throws<DomainException>(() => new WhatsAppChannel(" ", WhatsAppMode.LinkOnly, "79001234567", Guid.NewGuid(), true, Now));

    [Fact]
    public void Link_only_needs_a_phone_but_qr_mode_gets_it_from_whatsapp()
    {
        Assert.Throws<DomainException>(() => new WhatsAppChannel("A", WhatsAppMode.LinkOnly, null, Guid.NewGuid(), true, Now));

        var channel = new WhatsAppChannel("A", WhatsAppMode.Gateway, null, Guid.NewGuid(), true, Now);
        Assert.False(channel.CanRedirect);
        channel.OnConnected("79001234567", "Support", Now);
        Assert.Equal("79001234567", channel.PhoneNumber);
        Assert.True(channel.IsConnected && channel.CanRedirect);

        // Подключённый номер не переключить в «только ссылка» и не переписать вручную.
        Assert.Throws<DomainException>(() => channel.Update("A", WhatsAppMode.LinkOnly, "79990000000", channel.SiteId, true));
        channel.Update("B", WhatsAppMode.Gateway, "79990000000", channel.SiteId, true);
        Assert.Equal("79001234567", channel.PhoneNumber);

        channel.OnDisconnected();
        Assert.False(channel.IsConnected);
        Assert.Equal("79001234567", channel.PhoneNumber);
    }

    [Theory]
    [InlineData("My code: AB23CD45", "AB23CD45", "")]
    [InlineData("my code: ab23cd45\nHello, I need help", "AB23CD45", "Hello, I need help")]
    [InlineData("Hello", null, "Hello")]
    [InlineData("My code: AB23", null, "My code: AB23")]
    public void Link_code_is_found_and_cut_out(string text, string? code, string rest)
    {
        var (found, remaining) = WhatsAppConversationService.ExtractCode(text);
        Assert.Equal(code, found);
        Assert.Equal(rest, remaining);
    }

    [Fact]
    public void Whatsapp_identity_keeps_the_chat_to_reply_to()
    {
        var channelId = Guid.NewGuid();
        var identity = ClientIdentity.CreateWhatsApp(channelId, "79001234567@s.whatsapp.net", Guid.NewGuid(), Now);
        Assert.True(identity.TryGetWhatsAppChat(out var id, out var jid));
        Assert.Equal(channelId, id);
        Assert.Equal("79001234567@s.whatsapp.net", jid);
        Assert.False(ClientIdentity.CreateTelegram(channelId, 42, Guid.NewGuid(), Now).TryGetWhatsAppChat(out _, out _));
    }

    [Theory]
    [InlineData("Ivan", "79001234567", "Ivan (+79001234567)")]
    [InlineData("Ivan", null, "Ivan")]
    [InlineData(null, "79001234567", "+79001234567")]
    [InlineData(" ", null, "WhatsApp")]
    public void Display_name_combines_profile_name_and_phone(string? name, string? phone, string expected) =>
        Assert.Equal(expected, WhatsAppConversationService.DisplayName(
            new WhatsAppIncoming("m", "x@lid", phone, name, "", Array.Empty<FileUpload>(), null)));
}
