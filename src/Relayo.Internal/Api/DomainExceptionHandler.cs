using Microsoft.AspNetCore.Diagnostics;
using Relayo.Core;

namespace Relayo.Internal.Api;

/// <summary>Ошибки предметной области отдаются как ProblemDetails с кодом ошибки.</summary>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainException domain)
            return false;

        http.Response.StatusCode = domain.Code switch
        {
            ErrorCodes.TicketNotFound or ErrorCodes.ChatNotFound or ErrorCodes.ClientNotFound
                or ErrorCodes.SiteNotFound or ErrorCodes.KeyNotFound or ErrorCodes.UserNotFound
                or ErrorCodes.WidgetNotFound or ErrorCodes.BotNotFound or ErrorCodes.WhatsAppChannelNotFound
                or ErrorCodes.EmailChannelNotFound
                or ErrorCodes.RuleNotFound or ErrorCodes.GroupNotFound => StatusCodes.Status404NotFound,
            ErrorCodes.TicketClosed or ErrorCodes.AliasRequired or ErrorCodes.SiteCodeTaken
                or ErrorCodes.LoginTaken or ErrorCodes.LastAdmin or ErrorCodes.BotTaken or ErrorCodes.PhoneTaken
                or ErrorCodes.MailboxTaken or ErrorCodes.ClientIdTaken
                or ErrorCodes.HasHistory or ErrorCodes.GroupNameTaken => StatusCodes.Status409Conflict,
            ErrorCodes.TelegramUnavailable or ErrorCodes.StorageUnavailable
                or ErrorCodes.WhatsAppGatewayUnavailable or ErrorCodes.UsedeskUnavailable => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status400BadRequest,
        };
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails =
            {
                Title = domain.Message,
                Status = http.Response.StatusCode,
                Extensions = { ["code"] = domain.Code },
            },
        });
    }
}
