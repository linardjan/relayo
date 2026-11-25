namespace Relayo.Internal.Channels.Telegram;

public enum TelegramUpdatesMode
{
    /// <summary>Telegram сам присылает обновления на публичный сервис. Нужен публичный HTTPS-адрес.</summary>
    Webhook,

    /// <summary>
    /// Внутренний сервис сам забирает обновления (getUpdates). Для разработки: работает без публичного адреса.
    /// Только один экземпляр сервиса, иначе Telegram отвечает 409.
    /// </summary>
    Polling,
}

/// <param name="ApiBaseUrl">Адрес Bot API. Можно заменить на свой Bot API server или заглушку в тестах.</param>
/// <param name="ProxyUrl">HTTP-прокси для выхода к api.telegram.org из внутренней сети, если его требует ИБ.</param>
/// <remarks>
/// Сами боты здесь не задаются: их может быть много и они меняются, поэтому их подключают
/// в админке («Channels → Add channel → Telegram bot»), а токены хранятся в БД зашифрованными.
/// </remarks>
public sealed record TelegramSettings(
    TelegramUpdatesMode Mode,
    Uri ApiBaseUrl,
    Uri? ProxyUrl);

/// <summary>Что бот отвечает сам. Тексты на английском, как и виджет.</summary>
internal static class TelegramTexts
{
    public const string Greeting = "Hi! Write your question here and our support team will reply in this chat.";

    public const string Linked = "You're connected! Continue the conversation here: our support team will reply in this chat.";

    public const string LinkExpired =
        "This link has expired. You can write your question right here, or go back to the website and tap "
        + "“Continue in Telegram” again to keep your chat history.";

    public const string AttachmentsNotSupported =
        "Sorry, files aren't supported yet. Please describe your question in a text message.";
}
