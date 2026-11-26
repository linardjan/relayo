using System.Text;

namespace Relayo.Core.Files;

/// <summary>
/// Ограничения на вложения из .env (ATTACHMENTS_*): суммарный размер файлов одного сообщения,
/// разрешённые типы и число файлов.
/// </summary>
public sealed record AttachmentLimits(long MaxBytes, IReadOnlyCollection<string> AllowedTypes, int MaxFiles = AttachmentLimits.DefaultMaxFiles)
{
    public const int DefaultMaxFiles = 10;
}

/// <summary>Файл, как его прислали: имя и тип назвал отправитель, им не доверяем.</summary>
public sealed record FileUpload(string FileName, string? ContentType, byte[] Content);

/// <summary>Файл после проверки: имя очищено, тип определён и сверен с содержимым.</summary>
public sealed record AcceptedFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Проверка вложений на сервере (п. 3.5 ТЗ): размер, число, тип. Тип берётся из присланного
/// или по расширению и сверяется с первыми байтами файла, чтобы, например, страница HTML
/// не прошла под видом картинки.
/// </summary>
public static class AttachmentPolicy
{
    public const int FileNameMaxLength = 200;

    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4",
        [".zip"] = "application/zip",
    };

    public static IReadOnlyList<AcceptedFile> Check(IReadOnlyList<FileUpload> files, AttachmentLimits limits)
    {
        if (files.Count > limits.MaxFiles)
            throw new DomainException(ErrorCodes.TooManyAttachments, $"Attach up to {limits.MaxFiles} files to one message");
        var total = files.Sum(f => (long)f.Content.Length);
        if (total > limits.MaxBytes)
            throw new DomainException(ErrorCodes.AttachmentTooLarge,
                $"Files of one message must be up to {limits.MaxBytes / (1024 * 1024)} MB in total");

        var allowed = new HashSet<string>(limits.AllowedTypes, StringComparer.OrdinalIgnoreCase);
        var result = new List<AcceptedFile>(files.Count);
        foreach (var file in files)
        {
            var name = CleanFileName(file.FileName);
            if (file.Content.Length == 0)
                throw new DomainException(ErrorCodes.InvalidInput, $"File '{name}' is empty");

            var type = ResolveType(name, file.ContentType, allowed);
            if (type is null || !MatchesContent(type, file.Content))
                throw new DomainException(ErrorCodes.AttachmentTypeNotAllowed, $"File type of '{name}' is not allowed");
            result.Add(new AcceptedFile(name, type, file.Content));
        }
        return result;
    }

    /// <summary>Только имя файла, без пути и служебных символов: оно попадёт в заголовок при скачивании.</summary>
    public static string CleanFileName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (!char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?'))
                clean.Append(c);
        }
        name = clean.ToString().Trim().TrimStart('.');
        if (name.Length == 0)
            return "file";
        if (name.Length <= FileNameMaxLength)
            return name;
        // Длинное имя обрезаем, но сохраняем расширение.
        var extension = Path.GetExtension(name);
        if (extension.Length > 16)
            extension = string.Empty;
        return name[..(FileNameMaxLength - extension.Length)] + extension;
    }

    /// <summary>Присланный тип, если он разрешён; иначе — тип по расширению, если разрешён он.</summary>
    internal static string? ResolveType(string fileName, string? declared, IReadOnlySet<string> allowed)
    {
        var type = declared?.Split(';')[0].Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(type) && allowed.Contains(type))
            return type;
        return TypesByExtension.TryGetValue(Path.GetExtension(fileName), out var byExtension) && allowed.Contains(byExtension)
            ? byExtension
            : null;
    }

    /// <summary>Сверяет начало файла с сигнатурой типа. Для типов без сигнатуры проверка не нужна.</summary>
    internal static bool MatchesContent(string type, ReadOnlySpan<byte> content) => type switch
    {
        "image/jpeg" => content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/png" => content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/gif" => content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8),
        "image/webp" => content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8),
        "application/pdf" => content.StartsWith("%PDF"u8),
        // docx, xlsx — zip-архивы; doc, xls — составной документ OLE.
        "application/zip"
            or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => content.StartsWith("PK\u0003\u0004"u8),
        "application/msword" or "application/vnd.ms-excel" => content.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]),
        "audio/ogg" => content.StartsWith("OggS"u8),
        "audio/mpeg" => content.StartsWith("ID3"u8) || (content.Length > 1 && content[0] == 0xFF && (content[1] & 0xE0) == 0xE0),
        "audio/mp4" or "video/mp4" => content.Length >= 8 && content[4..8].SequenceEqual("ftyp"u8),
        // Текст не должен содержать нулевых байтов: так под видом .txt не пройдёт исполняемый файл.
        "text/plain" or "text/csv" => !content[..Math.Min(content.Length, 8192)].Contains((byte)0),
        _ => true,
    };

    /// <summary>Картинки браузер может показать прямо в чате, остальное скачивается.</summary>
    public static bool IsInlineImage(string contentType) =>
        contentType is "image/jpeg" or "image/png" or "image/gif" or "image/webp";
}
