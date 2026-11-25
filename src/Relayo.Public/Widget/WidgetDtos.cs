using Relayo.Contracts.Grpc;
using Relayo.Contracts.Messages;

namespace Relayo.Public.Widget;

public sealed record WidgetConfigDto(
    string WidgetId,
    string Title,
    string Greeting,
    string AccentColor,
    string Position,
    string IdentificationMode,
    string DefaultLocale,
    int HistoryDays,
    string? TelegramBot)
{
    public static WidgetConfigDto From(WidgetConfig c) => new(
        c.WidgetId, c.Title, c.Greeting, c.AccentColor, c.Position, c.IdentificationMode, c.DefaultLocale, c.HistoryDays,
        string.IsNullOrEmpty(c.TelegramBot) ? null : c.TelegramBot);
}

public sealed record MessengerLinkDto(string Url, DateTimeOffset ExpiresAt);

public sealed record SessionDto(string VisitorToken, bool Verified, IReadOnlyList<WidgetMessageDto> Messages);

public sealed record IdentifyTokenRequest(string Token);

public sealed record IdentifyDirectRequest(string ClientId, string? Product, string? Locale);

public sealed record SendMessageRequest(string Text, string? ClientMessageId);

internal static class WidgetMapping
{
    public static WidgetMessageDto ToDto(WidgetMessage m) => new(
        Guid.Parse(m.Id),
        Guid.Parse(m.ChatId),
        m.Author,
        string.IsNullOrEmpty(m.AuthorName) ? null : m.AuthorName,
        m.Text,
        DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAtUnixMs),
        string.IsNullOrEmpty(m.ClientMessageId) ? null : m.ClientMessageId);
}
