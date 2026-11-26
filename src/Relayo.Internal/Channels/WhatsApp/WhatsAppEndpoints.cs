using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Core.Conversations;
using Relayo.Core.Files;
using Relayo.Internal.Api;
using Relayo.Internal.Messaging;

namespace Relayo.Internal.Channels.WhatsApp;

/// <summary>Файл от шлюза: содержимое в base64.</summary>
public sealed record GatewayFile(string FileName, string? ContentType, string Content);

/// <summary>Входящее сообщение от шлюза (см. src/Relayo.WhatsApp/src/internal.ts).</summary>
public sealed record GatewayMessage(
    string MessageId,
    string ChatJid,
    string? Phone,
    string? PushName,
    string? Text,
    IReadOnlyList<GatewayFile>? Files,
    string? Unsupported);

/// <summary>Шлюз сообщает: номер подключён (connected) или из него вышли в телефоне (logged_out).</summary>
public sealed record GatewayStatus(string Status, string? Phone, string? Name);

/// <summary>Состояние подключения для страницы канала. Gateway: configured — шлюз настроен в .env.</summary>
public sealed record WhatsAppConnectionDto(
    bool Configured,
    string Status,
    string? Qr,
    DateTimeOffset? QrExpiresAt,
    string? Phone,
    string? Name,
    string? Reason);

/// <summary>Что номер сам отвечает клиенту. Тексты на английском, как и виджет.</summary>
internal static class WhatsAppTexts
{
    public const string Linked = "You're connected! Continue the conversation here: our support team will reply in this chat.";

    public const string LinkExpired =
        "This code has expired. You can write your question right here, or go back to the website and tap "
        + "the WhatsApp button again to keep your chat history.";
}

internal static class WhatsAppEndpoints
{
    /// <summary>Вызовы шлюза WhatsApp: входящие сообщения и смена состояния. Не JWT, а общий ключ.</summary>
    public static RouteGroupBuilder MapWhatsAppGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var gateway = app.MapGroup("/api/whatsapp-gateway/channels/{id:guid}")
            .AddEndpointFilter(async (context, next) =>
            {
                var settings = context.HttpContext.RequestServices.GetService<WhatsAppGatewaySettings>();
                var given = context.HttpContext.Request.Headers[WhatsAppGateway.KeyHeader].ToString();
                if (settings is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given),
                        Encoding.UTF8.GetBytes(settings.Key)))
                    return Results.Unauthorized();
                return await next(context);
            });

        gateway.MapPost("/status", async (Guid id, GatewayStatus status, WhatsAppChannelService channels,
            ILogger<WhatsAppGateway> logger, CancellationToken ct) =>
        {
            var connected = status.Status == "connected";
            if (!connected && status.Status != "logged_out")
                return Results.BadRequest();
            if (!await channels.OnGatewayStatusAsync(id, connected, status.Phone, status.Name, ct))
                return Results.NotFound();
            logger.LogInformation("WhatsApp channel {ChannelId}: {Status}", id, status.Status);
            return Results.NoContent();
        });

        gateway.MapPost("/messages", async (Guid id, GatewayMessage message, WhatsAppMessageProcessor processor,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(message.MessageId) || string.IsNullOrWhiteSpace(message.ChatJid)
                || message.MessageId.Length > 100 || message.ChatJid.Length > 100)
                return Results.BadRequest();
            return await processor.ProcessAsync(id, message, ct) ? Results.NoContent() : Results.NotFound();
        }).WithMetadata(new RequestSizeLimitAttribute(
            // Файлы приходят в base64: на треть больше самих файлов.
            app.ServiceProvider.GetRequiredService<AttachmentLimits>().MaxBytes * 4 / 3 + FormFiles.FormOverheadBytes));

        return gateway;
    }

    /// <summary>Подключение номера по QR-коду: страница канала в админке (только администратор).</summary>
    public static RouteGroupBuilder MapWhatsAppConnectionEndpoints(this RouteGroupBuilder settings)
    {
        settings.MapGet("/whatsapp-channels/{id:guid}/connection", async (Guid id, WhatsAppChannelService channels,
            WhatsAppGateway gateway, CancellationToken ct) =>
        {
            var channel = await channels.FindAsync(id, ct);
            if (!gateway.IsConfigured)
                return new WhatsAppConnectionDto(false, "disconnected", null, null, channel.PhoneNumber, channel.AccountName, null);
            try
            {
                return ToDto(await gateway.GetSessionAsync(id, ct));
            }
            catch (DomainException ex) when (ex.Code == ErrorCodes.WhatsAppGatewayUnavailable)
            {
                return new WhatsAppConnectionDto(true, "unavailable", null, null, channel.PhoneNumber, channel.AccountName, null);
            }
        });

        settings.MapPost("/whatsapp-channels/{id:guid}/connect", async (Guid id, WhatsAppChannelService channels,
            WhatsAppGateway gateway, CancellationToken ct) =>
        {
            var channel = await channels.FindAsync(id, ct);
            if (channel.Mode != WhatsAppMode.Gateway)
                throw new DomainException(ErrorCodes.InvalidInput, "Switch the channel to QR connection first");
            return ToDto(await gateway.StartAsync(id, ct));
        });

        settings.MapPost("/whatsapp-channels/{id:guid}/disconnect", async (Guid id, WhatsAppChannelService channels,
            WhatsAppGateway gateway, CancellationToken ct) =>
        {
            await channels.FindAsync(id, ct);
            await gateway.LogoutAsync(id, ct);
            await channels.MarkDisconnectedAsync(id, ct);
            return await channels.GetAsync(id, ct);
        });

        return settings;
    }

    private static WhatsAppConnectionDto ToDto(WhatsAppSession s) =>
        new(true, s.Status, s.Qr, s.QrExpiresAt, s.Phone, s.Name, s.Reason);
}

/// <summary>Входящее сообщение WhatsApp: в тикет, операторам, правилам; про код из виджета — ответ клиенту.</summary>
public sealed class WhatsAppMessageProcessor(
    WhatsAppConversationService whatsApp,
    ConversationEvents events,
    WhatsAppOutbox outbox,
    ILogger<WhatsAppMessageProcessor> logger)
{
    /// <returns>false — канала нет (удалён): шлюз выйдет из WhatsApp.</returns>
    public async Task<bool> ProcessAsync(Guid channelId, GatewayMessage message, CancellationToken ct)
    {
        var channel = await whatsApp.FindChannelAsync(channelId, ct);
        if (channel is null)
            return false;
        if (channel is not { IsEnabled: true, Mode: WhatsAppMode.Gateway })
        {
            // Выключенный номер сообщения не принимает. Сессия в шлюзе остаётся: включат — снова заработает.
            logger.LogInformation("WhatsApp channel {ChannelId} is disabled: an incoming message is ignored", channelId);
            return true;
        }

        var files = new List<FileUpload>();
        foreach (var file in message.Files ?? [])
        {
            try
            {
                files.Add(new FileUpload(file.FileName, file.ContentType, Convert.FromBase64String(file.Content)));
            }
            catch (FormatException)
            {
                logger.LogWarning("WhatsApp gateway sent a file that is not base64: skipped");
            }
        }

        var received = await whatsApp.ReceiveAsync(channel, new WhatsAppIncoming(message.MessageId, message.ChatJid,
            message.Phone, message.PushName, message.Text ?? string.Empty, files, message.Unsupported), ct);

        foreach (var posted in received.Posted.Where(p => !p.Duplicate))
            await events.ClientMessagePostedAsync(posted, Channel.WhatsApp, ct);
        if (received.Link == WhatsAppLinkResult.Linked)
            await outbox.SendTextAsync(channel.Id, message.ChatJid, WhatsAppTexts.Linked);
        else if (received.Link == WhatsAppLinkResult.Expired)
            await outbox.SendTextAsync(channel.Id, message.ChatJid, WhatsAppTexts.LinkExpired);
        if (received.Link != WhatsAppLinkResult.None)
            logger.LogInformation("WhatsApp link code on channel {ChannelId}: {Result}", channelId, received.Link);
        return true;
    }
}
