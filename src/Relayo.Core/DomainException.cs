namespace Relayo.Core;

/// <summary>Ошибка, понятная вызывающей стороне: её код уходит клиенту API как есть.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ErrorCodes
{
    public const string WidgetNotFound = "widget_not_found";
    public const string VisitorNotFound = "visitor_not_found";
    public const string WrongMode = "wrong_identification_mode";
    public const string InvalidToken = "invalid_client_token";
    public const string SiteMismatch = "site_mismatch";
    public const string UnknownProduct = "unknown_product";
    public const string InvalidInput = "invalid_input";
    public const string TicketNotFound = "ticket_not_found";
    public const string ChatNotFound = "chat_not_found";
    public const string TicketClosed = "ticket_closed";
    public const string AliasRequired = "alias_required";
    public const string ClientNotFound = "client_not_found";
    public const string SiteNotFound = "site_not_found";
    public const string SiteCodeTaken = "site_code_taken";
    public const string KeyNotFound = "key_not_found";
    public const string UserNotFound = "user_not_found";
    public const string LoginTaken = "login_taken";
    public const string WeakPassword = "weak_password";
    public const string LastAdmin = "last_admin";
    public const string BotNotFound = "bot_not_found";
    public const string WhatsAppChannelNotFound = "whatsapp_channel_not_found";
    public const string PhoneTaken = "phone_taken";
    public const string EmailChannelNotFound = "email_channel_not_found";
    public const string MailboxTaken = "mailbox_taken";
    public const string UsedeskUnavailable = "usedesk_unavailable";
    public const string WhatsAppNotConnected = "whatsapp_not_connected";
    public const string WhatsAppGatewayNotConfigured = "whatsapp_gateway_not_configured";
    public const string WhatsAppGatewayUnavailable = "whatsapp_gateway_unavailable";
    public const string RuleNotFound = "rule_not_found";
    public const string StorageNotConfigured = "storage_not_configured";
    public const string HasHistory = "has_history";
    public const string GroupNotFound = "group_not_found";
    public const string GroupNameTaken = "group_name_taken";
    public const string StorageUnavailable = "storage_unavailable";
    public const string BotTaken = "bot_taken";
    public const string InvalidBotToken = "invalid_bot_token";
    public const string TelegramUnavailable = "telegram_unavailable";
    public const string WebhookRequiresHttps = "webhook_requires_https";
    public const string TelegramNotConnected = "telegram_not_connected";
    public const string WebhookForbidden = "webhook_forbidden";
    public const string AttachmentTooLarge = "attachment_too_large";
    public const string AttachmentTypeNotAllowed = "attachment_type_not_allowed";
    public const string TooManyAttachments = "too_many_attachments";
    public const string AttachmentsDisabled = "attachments_disabled";
}
