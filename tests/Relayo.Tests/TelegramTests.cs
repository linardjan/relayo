using Relayo.Core.Channels;
using Relayo.Core.Clients;
using Relayo.Internal.Channels.Telegram;
using Relayo.ServiceDefaults;

namespace Relayo.Tests;

public class TelegramTests
{
    // Выдуманный токен нужного формата: настоящие токены в тестах не храним.
    private const string FakeToken = "111111111:FAKE_token_for_tests_only_0123456";

    [Theory]
    [InlineData("/start", true, null)]
    [InlineData("/start abc_DEF-123", true, "abc_DEF-123")]
    [InlineData("  /start   abc  ", true, "abc")]
    [InlineData("/start@SupportBot abc", true, "abc")]
    [InlineData("/start@SupportBot", true, null)]
    [InlineData("/started", false, null)]
    [InlineData("hello /start abc", false, null)]
    public void Start_command_is_parsed(string text, bool isStart, string? payload)
    {
        Assert.Equal(isStart, TelegramUpdateProcessor.TryParseStart(text, out var actual));
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void Telegram_identity_knows_where_to_reply()
    {
        var botId = Guid.CreateVersion7();
        var identity = ClientIdentity.CreateTelegram(botId, 123456789, Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        Assert.True(identity.TryGetTelegramChat(out var actualBot, out var chatId));
        Assert.Equal(botId, actualBot);
        Assert.Equal(123456789, chatId);
    }

    [Fact]
    public void Visitor_identity_is_not_a_telegram_chat()
    {
        var visitor = ClientIdentity.CreateVisitor(Guid.CreateVersion7(), Guid.CreateVersion7(), "hash", DateTimeOffset.UtcNow);
        Assert.False(visitor.TryGetTelegramChat(out _, out _));
    }

    [Fact]
    public void Sender_display_name_combines_name_and_username()
    {
        Assert.Equal("Ivan Petrov (@ivan)", new TelegramSender(1, 1, "Ivan", "Petrov", "ivan", null).DisplayName);
        Assert.Equal("@ivan", new TelegramSender(1, 1, null, null, "ivan", null).DisplayName);
        Assert.Equal("Telegram 42", new TelegramSender(42, 42, null, " ", null, null).DisplayName);
    }

    [Theory]
    [InlineData(FakeToken, true)]
    [InlineData("111111111:short", false)]
    [InlineData("abc:FAKE_token_for_tests_only_0123456", false)]
    [InlineData(FakeToken + "/../x", false)]
    public void Bot_token_format_is_checked(string token, bool valid) =>
        Assert.Equal(valid, TelegramBotApi.IsValidToken(token));

    [Fact]
    public void Bot_token_is_masked_in_traces()
    {
        var url = $"https://api.telegram.org/bot{FakeToken}/sendMessage";
        Assert.Equal("https://api.telegram.org/bot***/sendMessage", SecretRedactingProcessor.Redact(url));
    }

    [Fact]
    public void Operator_reply_is_html_escaped()
    {
        Assert.Equal("&lt;b&gt;x&lt;/b&gt; &amp; y", TelegramOutbox.Escape("<b>x</b> & y"));
    }
}
