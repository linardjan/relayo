namespace Relayo.Contracts.Messages;

/// <summary>
/// Сообщение для виджета: так его видит посетитель.
/// Для оператора указывается только псевдоним (п. 3.1 ТЗ).
/// </summary>
public sealed record WidgetMessageDto(
    Guid Id,
    Guid ChatId,
    string Author,
    string? AuthorName,
    string Text,
    DateTimeOffset CreatedAt,
    string? ClientMessageId,
    IReadOnlyList<WidgetAttachmentDto>? Attachments = null);

/// <summary>Вложение сообщения: имя, тип, размер и временная ссылка на скачивание.</summary>
public sealed record WidgetAttachmentDto(Guid Id, string FileName, string ContentType, long Size, string? Url);

/// <summary>
/// Внутренний сервис сообщает публичным, что появилось сообщение, которое надо
/// доставить в открытые виджеты. Получают все экземпляры публичного сервиса,
/// каждый отправляет его своим подключённым посетителям.
/// </summary>
/// <param name="VisitorIds">Посетители, которые писали в этом чате.</param>
/// <param name="VerifiedClientId">Если чат подтверждённый — клиент, всем подтверждённым сессиям которого он виден.</param>
public sealed record WidgetMessagePublished(
    WidgetMessageDto Message,
    IReadOnlyList<Guid> VisitorIds,
    Guid? VerifiedClientId);
