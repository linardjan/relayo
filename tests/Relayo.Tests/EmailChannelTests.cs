using System.Net.Sockets;
using MailKit.Security;
using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Internal.Channels.Email;

namespace Relayo.Tests;

public class EmailChannelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static MailServer Server(string email = "support@example.test", string host = "imap.example.test", int port = 993,
        string? username = null, string password = "protected") =>
        MailServer.Create("Incoming", email, host, port, MailSecurity.Ssl, username, password);

    [Fact]
    public void Server_fields_are_normalized()
    {
        var server = Server(email: " support@example.test ", host: " IMAP.Example.Test. ", username: "SUPPORT@example.test");
        Assert.Equal("support@example.test", server.Email);
        Assert.Equal("imap.example.test", server.Host);
        // Имя совпадает с адресом — отдельно не хранится.
        Assert.Null(server.Username);
        Assert.Equal("support@example.test", server.Login);

        Assert.Equal("EXAMPLE\\support", Server(username: " EXAMPLE\\support ").Login);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Support <support@example.test>")]
    public void Bad_email_is_rejected(string email) =>
        Assert.Throws<DomainException>(() => Server(email: email));

    [Theory]
    [InlineData("")]
    [InlineData("imaps://imap.example.test")]
    [InlineData("imap.example.test:993")]
    [InlineData("imap.example.test/inbox")]
    [InlineData("imap example.test")]
    public void Bad_host_is_rejected(string host) =>
        Assert.Throws<DomainException>(() => Server(host: host));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Bad_port_is_rejected(int port) =>
        Assert.Throws<DomainException>(() => Server(port: port));

    [Fact]
    public void Name_is_required() =>
        Assert.Throws<DomainException>(() =>
            new EmailChannel(" ", Guid.NewGuid(), true, false, EmailQuoting.SendToClient, Server(), Server(), Now));

    [Fact]
    public void Changing_a_server_resets_the_check()
    {
        var channel = new EmailChannel("Support", Guid.NewGuid(), true, false, EmailQuoting.SendToClient, Server(), Server(), Now);
        channel.RecordCheck(true, "ok", Now);

        channel.SetServers(Server(), Server());
        Assert.True(channel.LastCheckOk);

        channel.SetServers(Server(port: 143), Server());
        Assert.Null(channel.LastCheckOk);
        Assert.Null(channel.LastCheckMessage);
    }

    [Fact]
    public void Check_errors_tell_what_to_fix()
    {
        var server = new MailServerCredentials("imap.example.test", 993, MailSecurity.StartTls, "support", "fake-password");
        Assert.Equal("wrong username or password.", EmailChannelChecker.Describe(new AuthenticationException(), server));
        Assert.Contains("imap.example.test:993", EmailChannelChecker.Describe(new SocketException((int)SocketError.ConnectionRefused), server));
        Assert.Contains("not found", EmailChannelChecker.Describe(new SocketException((int)SocketError.HostNotFound), server));
        Assert.Contains("15 seconds", EmailChannelChecker.Describe(new TimeoutException(), server));
        Assert.Contains("STARTTLS", EmailChannelChecker.Describe(new NotSupportedException(), server));
        Assert.DoesNotContain("fake-password", EmailChannelChecker.Describe(new InvalidOperationException("boom"), server));
    }
}
