using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Relayo.Contracts.Grpc;
using Relayo.Core;
using Relayo.Core.Channels;
using Relayo.Core.Conversations;
using Relayo.Core.Files;
using Relayo.Core.Security;
using Relayo.Core.Sites;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Messaging;
using MessengerLink = Relayo.Contracts.Grpc.MessengerLink;
using VisitorSession = Relayo.Contracts.Grpc.VisitorSession;

namespace Relayo.Internal.Grpc;

internal sealed class InternalGatewayService(
    WidgetConversationService widgets,
    TelegramConversationService telegram,
    TelegramUpdateProcessor telegramUpdates,
    ConversationEvents events,
    WidgetPublisher publisher,
    SecretProtector protector,
    TimeProvider time,
    ILogger<InternalGatewayService> logger) : InternalGateway.InternalGatewayBase
{
    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingReply
        {
            Service = "relayo-internal",
            ServerTimeUnixMs = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });

    public override async Task<AllowedOrigins> GetAllowedOrigins(GetAllowedOriginsRequest request,
        ServerCallContext context)
    {
        var reply = new AllowedOrigins();
        reply.Origins.AddRange(await widgets.GetAllowedOriginsAsync(context.CancellationToken));
        return reply;
    }

    public override Task<WidgetConfig> GetWidget(WidgetRequest request, ServerCallContext context) =>
        Run(async ct =>
        {
            var widget = await widgets.GetWidgetAsync(ParseId(request.WidgetId), ct);
            // Те же настройки, что публикуются в файл виджета: виджет без S3 получает их по API.
            var snapshot = publisher.Snapshot(widget, await telegram.GetWidgetBotUsernameAsync(widget, ct));
            var config = new WidgetConfig
            {
                WidgetId = snapshot.WidgetId,
                SiteCode = widget.Site.Code,
                IdentificationMode = snapshot.IdentificationMode,
                Title = snapshot.Title,
                Greeting = snapshot.Greeting,
                AccentColor = snapshot.AccentColor,
                Position = snapshot.Position,
                DefaultLocale = snapshot.DefaultLocale,
                HistoryDays = snapshot.HistoryDays,
                TelegramBot = snapshot.TelegramBot ?? string.Empty,
                AllowAttachments = snapshot.MaxAttachmentBytes > 0,
                MaxAttachmentBytes = snapshot.MaxAttachmentBytes,
                OptionsJson = JsonSerializer.Serialize(snapshot.Options, WidgetPublisher.Json),
                ButtonIconUrl = snapshot.ButtonIconUrl ?? string.Empty,
            };
            config.AllowedOrigins.AddRange(widget.Site.AllowedOrigins);
            return config;
        }, context);

    public override Task<VisitorSession> StartVisitor(StartVisitorRequest request, ServerCallContext context) =>
        Run(async ct =>
        {
            var widget = await widgets.GetWidgetAsync(ParseId(request.WidgetId), ct);
            var session = await widgets.StartVisitorAsync(widget, request.VisitorToken, request.CreateIfMissing, ct);
            return ToSession(session);
        }, context);

    public override Task<VisitorSession> Identify(IdentifyRequest request, ServerCallContext context) =>
        Run(async ct =>
        {
            var widget = await widgets.GetWidgetAsync(ParseId(request.WidgetId), ct);
            var session = request.ClaimCase switch
            {
                IdentifyRequest.ClaimOneofCase.SignedToken =>
                    await widgets.IdentifyWithTokenAsync(widget, request.VisitorToken, request.SignedToken, ct),
                IdentifyRequest.ClaimOneofCase.Direct =>
                    await widgets.IdentifyDirectAsync(widget, request.VisitorToken, request.Direct.ClientId,
                        request.Direct.Product, request.Direct.Locale, ct),
                _ => throw new DomainException(ErrorCodes.InvalidInput, "Identity claim is missing"),
            };
            return ToSession(session);
        }, context);

    public override Task<WidgetMessage> SendVisitorMessage(SendVisitorMessageRequest request,
        ServerCallContext context) =>
        Run(async ct =>
        {
            var widget = await widgets.GetWidgetAsync(ParseId(request.WidgetId), ct);
            var files = request.Files
                .Select(f => new FileUpload(f.FileName, f.ContentType, f.Content.ToByteArray()))
                .ToList();
            var posted = await widgets.SendAsync(widget, request.VisitorToken, request.Text,
                string.IsNullOrEmpty(request.ClientMessageId) ? null : request.ClientMessageId, files, ct);

            // Другие вкладки и устройства клиента получают сообщение сразу, операторы — тоже.
            if (!posted.Duplicate)
                await events.ClientMessagePostedAsync(posted, Channel.Widget, ct);

            return ToGrpc(posted.Message);
        }, context);

    public override Task<WidgetHistory> GetVisitorHistory(VisitorRequest request, ServerCallContext context) =>
        Run(async ct =>
        {
            var widget = await widgets.GetWidgetAsync(ParseId(request.WidgetId), ct);
            var history = new WidgetHistory();
            history.Messages.AddRange((await widgets.GetHistoryAsync(widget, request.VisitorToken, ct)).Select(ToGrpc));
            return history;
        }, context);

    public override Task<MessengerLink> CreateTelegramLink(VisitorRequest request, ServerCallContext context) =>
        Run(async ct =>
        {
            var widget = await widgets.GetWidgetAsync(ParseId(request.WidgetId), ct);
            var link = await telegram.CreateLinkAsync(widget, request.VisitorToken, ct);
            return new MessengerLink { Url = link.Url, ExpiresAtUnixMs = link.ExpiresAt.ToUnixTimeMilliseconds() };
        }, context);

    public override Task<TelegramUpdateReply> HandleTelegramUpdate(TelegramUpdateRequest request,
        ServerCallContext context) =>
        Run(async ct =>
        {
            // Секрет webhook сверяется здесь: публичный сервис секретов ботов не знает.
            var bot = Guid.TryParse(request.BotId, out var botId) ? await telegram.FindBotAsync(botId, ct) : null;
            if (bot is not { IsEnabled: true } || !SecretMatches(request.Secret, protector.Unprotect(bot.ProtectedWebhookSecret)))
                throw new DomainException(ErrorCodes.WebhookForbidden, "Unknown bot or wrong webhook secret");

            TgUpdate? update;
            try
            {
                update = JsonSerializer.Deserialize<TgUpdate>(request.UpdateJson, TelegramBotApi.Json);
            }
            catch (JsonException ex)
            {
                // Повтор того же тела ничего не изменит: подтверждаем, чтобы Telegram не присылал его снова.
                logger.LogWarning("Telegram sent an update that could not be parsed: {Error}", ex.Message);
                return new TelegramUpdateReply();
            }
            if (update is not null)
                await telegramUpdates.ProcessAsync(bot, update, ct);
            return new TelegramUpdateReply();
        }, context);

    private static bool SecretMatches(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    internal static WidgetMessage ToGrpc(MessageView m)
    {
        var message = new WidgetMessage
        {
            Id = m.Id.ToString(),
            ChatId = m.ChatId.ToString(),
            Author = WidgetEvents.AuthorCode(m.Author),
            AuthorName = m.AuthorName ?? string.Empty,
            Text = m.Text,
            CreatedAtUnixMs = m.CreatedAt.ToUnixTimeMilliseconds(),
            ClientMessageId = m.ClientMessageId ?? string.Empty,
        };
        message.Attachments.AddRange((m.Attachments ?? []).Select(a => new WidgetAttachment
        {
            Id = a.Id.ToString(),
            FileName = a.FileName,
            ContentType = a.ContentType,
            Size = a.Size,
            Url = a.Url ?? string.Empty,
        }));
        return message;
    }

    private static VisitorSession ToSession(Core.Conversations.VisitorSession session) => new()
    {
        VisitorToken = session.Token,
        VisitorId = session.Visitor.Id.ToString(),
        Verified = session.Visitor.IsVerified,
        ClientId = session.Visitor.ClientId?.ToString() ?? string.Empty,
    };

    private static Guid ParseId(string value) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new DomainException(ErrorCodes.WidgetNotFound, "Widget not found");

    /// <summary>Переводит ошибки предметной области в статусы gRPC с кодом ошибки в detail.</summary>
    private static async Task<T> Run<T>(Func<CancellationToken, Task<T>> action, ServerCallContext context)
    {
        try
        {
            return await action(context.CancellationToken);
        }
        catch (DomainException ex)
        {
            var status = ex.Code switch
            {
                ErrorCodes.WidgetNotFound or ErrorCodes.VisitorNotFound => StatusCode.NotFound,
                ErrorCodes.InvalidToken => StatusCode.Unauthenticated,
                ErrorCodes.WrongMode or ErrorCodes.SiteMismatch => StatusCode.PermissionDenied,
                ErrorCodes.WebhookForbidden => StatusCode.Unauthenticated,
                ErrorCodes.TelegramNotConnected or ErrorCodes.AttachmentsDisabled => StatusCode.FailedPrecondition,
                ErrorCodes.StorageNotConfigured or ErrorCodes.StorageUnavailable => StatusCode.Unavailable,
                _ => StatusCode.InvalidArgument,
            };
            throw new RpcException(new Status(status, ex.Code), ex.Message);
        }
    }
}
