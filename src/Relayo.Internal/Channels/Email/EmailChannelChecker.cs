using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Relayo.Core.Channels;

namespace Relayo.Internal.Channels.Email;

/// <summary>
/// Проверка почтового канала: вход в ящик по IMAP (и открытие «Входящих») и вход на SMTP-сервер, без отправки писем.
/// Результат сохраняется в канале и виден на его странице.
/// </summary>
internal sealed class EmailChannelChecker(EmailChannelService service, ILogger<EmailChannelChecker> logger)
{
    /// <summary>Ожидание ответа сервера на каждый шаг (подключение, вход).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<EmailChannelView> CheckAsync(Guid channelId, CancellationToken ct)
    {
        var (imap, smtp) = await service.GetCredentialsAsync(channelId, ct);
        // Оба сервера проверяются одновременно: администратор ждёт не дольше одного тайм-аута.
        var results = await Task.WhenAll(CheckImapAsync(imap, ct), CheckSmtpAsync(smtp, ct));
        var ok = results.All(r => r.Ok);
        var message = $"Incoming (IMAP): {results[0].Text}\nOutgoing (SMTP): {results[1].Text}";
        if (!ok)
            logger.LogWarning("Email channel {ChannelId} check failed: {Result}", channelId, message);
        return await service.RecordCheckAsync(channelId, ok, message, ct);
    }

    private static async Task<(bool Ok, string Text)> CheckImapAsync(MailServerCredentials server, CancellationToken ct)
    {
        try
        {
            using var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
            await client.ConnectAsync(server.Host, server.Port, SocketOptions(server.Security), ct);
            await client.AuthenticateAsync(server.Login, server.Password, ct);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            var count = client.Inbox.Count;
            await client.DisconnectAsync(true, ct);
            return (true, $"connected, messages in the inbox: {count}.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, Describe(ex, server));
        }
    }

    private static async Task<(bool Ok, string Text)> CheckSmtpAsync(MailServerCredentials server, CancellationToken ct)
    {
        try
        {
            using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
            await client.ConnectAsync(server.Host, server.Port, SocketOptions(server.Security), ct);
            // Сервер без AUTH (релей во внутренней сети) принимает письма без входа.
            var auth = client.AuthenticationMechanisms.Count > 0;
            if (auth)
                await client.AuthenticateAsync(server.Login, server.Password, ct);
            await client.DisconnectAsync(true, ct);
            return (true, auth ? "connected." : "connected, the server does not ask for a password.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, Describe(ex, server));
        }
    }

    internal static SecureSocketOptions SocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.Ssl => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };

    /// <summary>Понятная администратору причина: что поправить в настройках.</summary>
    internal static string Describe(Exception ex, MailServerCredentials server) => ex switch
    {
        AuthenticationException => "wrong username or password.",
        SslHandshakeException => "TLS error: check the encryption and the port, or the server certificate is not trusted.",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
            $"server {server.Host} not found.",
        SocketException s => $"cannot connect to {server.Host}:{server.Port} ({s.SocketErrorCode}).",
        TimeoutException or OperationCanceledException =>
            $"{server.Host}:{server.Port} did not respond in {Timeout.TotalSeconds:0} seconds: check the port and the encryption.",
        NotSupportedException when server.Security == MailSecurity.StartTls => "the server does not support STARTTLS.",
        ProtocolException => "the server replied in an unexpected way: check the port and the encryption.",
        CommandException => $"the server refused: {ex.Message}",
        _ => ex.Message,
    };
}
