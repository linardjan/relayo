using Relayo.Core.Conversations;

namespace Relayo.Tests;

public class ConversationModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("https://shop.example/catalog?page=2#reviews", "https://shop.example/catalog?page=2")]
    [InlineData("  http://example.org/  ", "http://example.org/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://example.org/file", null)]
    [InlineData("/relative/path", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Page_url_accepts_only_absolute_http_addresses_without_fragment(string? input, string? expected) =>
        Assert.Equal(expected, Chat.NormalizePageUrl(input));

    [Fact]
    public void Long_page_url_is_cut_to_column_length()
    {
        var url = "https://example.org/?q=" + new string('a', 3000);
        Assert.Equal(Chat.PageUrlMaxLength, Chat.NormalizePageUrl(url)!.Length);
    }

    [Fact]
    public void Chat_keeps_the_first_page_url()
    {
        var chat = Chat.Start(Ticket.Open(Guid.NewGuid(), Now), verified: false, Now);

        chat.SetPageUrlIfEmpty("bad url");
        Assert.Null(chat.PageUrl);
        chat.SetPageUrlIfEmpty("https://example.org/first");
        chat.SetPageUrlIfEmpty("https://example.org/second");

        Assert.Equal("https://example.org/first", chat.PageUrl);
    }

    [Fact]
    public void Note_is_internal_and_reply_is_public()
    {
        var chat = Chat.Start(Ticket.Open(Guid.NewGuid(), Now), verified: true, Now);
        var operatorId = Guid.NewGuid();

        var note = Message.Note(chat, operatorId, "Client asked for a refund before", Now);
        var reply = Message.FromOperator(chat, operatorId, "Hello", Channel.Widget, Now);

        Assert.True(note.IsInternal);
        Assert.Equal(operatorId, note.OperatorId);
        Assert.False(reply.IsInternal);
    }

    [Fact]
    public void New_ticket_is_created_in_relayo_without_group()
    {
        var ticket = Ticket.Open(Guid.NewGuid(), Now);
        Assert.Equal(TicketSource.Relayo, ticket.Source);
        Assert.Null(ticket.GroupId);

        var groupId = Guid.NewGuid();
        ticket.SetGroup(groupId, Now.AddMinutes(1));
        Assert.Equal(groupId, ticket.GroupId);
        Assert.Equal(Now.AddMinutes(1), ticket.UpdatedAt);
    }
}
