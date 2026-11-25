using Microsoft.EntityFrameworkCore;
using Relayo.Core.Conversations;
using Relayo.Core.Data;
using Relayo.Core.Sites;

namespace Relayo.Core.Files;

/// <summary>
/// Вложение сообщения. Сам файл лежит в закрытом бакете S3 (S3_BUCKET_ATTACHMENTS) под ключом
/// <see cref="StorageKey"/>, здесь — имя, тип и размер. Скачивается по временной ссылке.
/// Таблица сообщений секционирована, поэтому связь с сообщением — по id без внешнего ключа.
/// </summary>
public sealed class Attachment
{
    public const int ContentTypeMaxLength = 128;
    public const int StorageKeyMaxLength = 200;

    private Attachment()
    {
    }

    public Guid Id { get; private set; }

    public Guid ChatId { get; private set; }

    public Guid MessageId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public string StorageKey { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    internal static Attachment For(Message message, StoredFile file) => new()
    {
        Id = file.Id,
        ChatId = message.ChatId,
        MessageId = message.Id,
        FileName = file.FileName,
        ContentType = file.ContentType,
        Size = file.Size,
        StorageKey = file.StorageKey,
        CreatedAt = message.CreatedAt,
    };
}

/// <summary>Файл уже лежит в S3, но ещё не привязан к сообщению.</summary>
public sealed record StoredFile(Guid Id, string FileName, string ContentType, long Size, string StorageKey);

/// <param name="Url">Временная ссылка на скачивание; null, если хранилище не настроено.</param>
public sealed record AttachmentView(Guid Id, string FileName, string ContentType, long Size, string? Url);

/// <summary>Вложения сообщений: проверка, загрузка в S3, привязка к сообщению и ссылки на скачивание.</summary>
public sealed class AttachmentService(RelayoDbContext db, IFileStorage storage, AttachmentLimits limits, TimeProvider time)
{
    public AttachmentLimits Limits => limits;

    /// <summary>
    /// Проверяет файлы и кладёт их в S3. Вызывается до транзакции с сообщением, чтобы не держать
    /// блокировки на время загрузки. Если сообщение потом не сохранится, файлы останутся в бакете без ссылок.
    /// </summary>
    public async Task<IReadOnlyList<StoredFile>> StoreAsync(IReadOnlyList<FileUpload> files, CancellationToken ct)
    {
        if (files.Count == 0)
            return [];
        if (!storage.IsConfigured)
            throw new DomainException(ErrorCodes.StorageNotConfigured, "File storage (S3) is not configured: files cannot be sent");

        var accepted = AttachmentPolicy.Check(files, limits);
        var now = time.GetUtcNow();
        var stored = new List<StoredFile>(accepted.Count);
        foreach (var file in accepted)
        {
            var id = Guid.CreateVersion7();
            // В ключе нет имени файла: имя может быть любым, а ключ должен быть простым.
            var key = $"{now:yyyy}/{now:MM}/{id:N}";
            await storage.PutAttachmentAsync(key, file.Content, file.ContentType, ct);
            stored.Add(new StoredFile(id, file.FileName, file.ContentType, file.Content.Length, key));
        }
        return stored;
    }

    /// <summary>Привязывает загруженные файлы к сообщению. Сохраняет вызывающий вместе с сообщением.</summary>
    public IReadOnlyList<AttachmentView> Attach(Message message, IReadOnlyList<StoredFile> files)
    {
        var views = new List<AttachmentView>(files.Count);
        foreach (var file in files)
        {
            var attachment = Attachment.For(message, file);
            db.Attachments.Add(attachment);
            views.Add(ToView(attachment));
        }
        return views;
    }

    /// <summary>Вложения сообщений чатов, сгруппированные по сообщению.</summary>
    public async Task<ILookup<Guid, AttachmentView>> ForChatsAsync(IReadOnlyCollection<Guid> chatIds, DateTimeOffset? since,
        CancellationToken ct)
    {
        if (chatIds.Count == 0)
            return Array.Empty<AttachmentView>().ToLookup(_ => Guid.Empty);
        var query = db.Attachments.AsNoTracking().Where(a => chatIds.Contains(a.ChatId));
        if (since is { } from)
            query = query.Where(a => a.CreatedAt >= from);
        var rows = await query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        return rows.ToLookup(a => a.MessageId, ToView);
    }

    public async Task<IReadOnlyList<Attachment>> ForMessageAsync(Guid messageId, CancellationToken ct) =>
        await db.Attachments.AsNoTracking().Where(a => a.MessageId == messageId).OrderBy(a => a.Id).ToListAsync(ct);

    public AttachmentView ToView(Attachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.Size, storage.AttachmentUrl(a.StorageKey, a.FileName, a.ContentType));

    /// <summary>Превью в списке тикетов: текст или имя файла.</summary>
    public static string Preview(string text, IReadOnlyList<StoredFile> files) =>
        text.Length > 0 || files.Count == 0
            ? text
            : files.Count == 1 ? $"📎 {files[0].FileName}" : $"📎 {files[0].FileName} +{files.Count - 1}";
}
