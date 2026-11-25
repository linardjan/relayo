using Relayo.Core;
using Relayo.Core.Files;

namespace Relayo.Internal.Api;

/// <summary>Файлы из multipart/form-data. Размер и тип проверяет <see cref="AttachmentPolicy"/>, здесь — только чтение.</summary>
internal static class FormFiles
{
    /// <summary>Запас на поля формы и заголовки частей сверх лимита на файлы.</summary>
    public const long FormOverheadBytes = 1024 * 1024;

    public static async Task<IReadOnlyList<FileUpload>> ReadAsync(IFormFileCollection files, long maxBytes, CancellationToken ct)
    {
        if (files.Sum(f => f.Length) > maxBytes)
            throw new DomainException(ErrorCodes.AttachmentTooLarge,
                $"Files of one message must be up to {maxBytes / (1024 * 1024)} MB in total");
        var result = new List<FileUpload>(files.Count);
        foreach (var file in files)
        {
            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            result.Add(new FileUpload(file.FileName, file.ContentType, buffer.ToArray()));
        }
        return result;
    }
}
