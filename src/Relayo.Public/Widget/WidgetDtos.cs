using System.Text.Json;
using Relayo.Contracts.Grpc;
using Relayo.Contracts.Messages;

namespace Relayo.Public.Widget;

/// <summary>Настройки виджета. Совпадают с настройками в опубликованном файле widget_&lt;ключ&gt;.js.</summary>
/// <param name="Options">Внешний вид и поведение: внутренний сервис присылает их готовым JSON.</param>
public sealed record WidgetConfigDto(
    string WidgetId,
    string Title,
    string Greeting,
    string AccentColor,
    string Position,
    string IdentificationMode,
    string DefaultLocale,
    int HistoryDays,
    string? TelegramBot,
    JsonElement? Options,
    string? ButtonIconUrl,
    long MaxAttachmentBytes)
{
    public static WidgetConfigDto From(WidgetConfig c) => new(
        c.WidgetId, c.Title, c.Greeting, c.AccentColor, c.Position, c.IdentificationMode, c.DefaultLocale, c.HistoryDays,
        string.IsNullOrEmpty(c.TelegramBot) ? null : c.TelegramBot,
        string.IsNullOrEmpty(c.OptionsJson) ? null : JsonDocument.Parse(c.OptionsJson).RootElement.Clone(),
        string.IsNullOrEmpty(c.ButtonIconUrl) ? null : c.ButtonIconUrl,
        c.AllowAttachments ? c.MaxAttachmentBytes : 0);
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
        string.IsNullOrEmpty(m.ClientMessageId) ? null : m.ClientMessageId,
        m.Attachments.Count == 0
            ? null
            : m.Attachments.Select(a => new WidgetAttachmentDto(Guid.Parse(a.Id), a.FileName, a.ContentType, a.Size,
                string.IsNullOrEmpty(a.Url) ? null : a.Url)).ToList());
}
