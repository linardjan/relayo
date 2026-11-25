using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Relayo.Core;
using Relayo.Core.Files;
using Relayo.Core.Sites;

namespace Relayo.Internal.Storage;

/// <summary>Подключение к S3 (MinIO компании; локально RustFS). Настраивается в .env: это инфраструктура, как БД.</summary>
/// <param name="PublicUrl">Адрес, по которому браузер открывает файлы (reverse proxy). На него выписываются временные ссылки.</param>
public sealed record StorageSettings(
    Uri Endpoint,
    string Region,
    string AccessKey,
    string SecretKey,
    bool ForcePathStyle,
    string WidgetsBucket,
    string AttachmentsBucket,
    Uri PublicUrl,
    TimeSpan PresignedUrlLifetime);

/// <summary>
/// Файлы в S3. Бакеты создаются при первой записи. В бакете виджетов читать файлы widget_* разрешено всем —
/// их загружают браузеры посетителей. Бакет вложений закрыт: файлы отдаются только по временным ссылкам.
/// </summary>
public sealed class S3FileStorage(StorageSettings? settings, ILogger<S3FileStorage> logger) : IFileStorage, IDisposable
{
    private readonly Lazy<AmazonS3Client>? client = settings is { } s ? new(() => Create(s, s.Endpoint)) : null;

    // Подпись временной ссылки включает адрес хоста, поэтому ссылки подписывает отдельный клиент
    // с публичным адресом. В сеть он не ходит: подпись считается локально.
    private readonly Lazy<AmazonS3Client>? signer = settings is { } s ? new(() => Create(s, s.PublicUrl)) : null;
    private readonly SemaphoreSlim bucketLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> readyBuckets = new();

    public bool IsConfigured => settings is not null;

    public async Task PutWidgetFileAsync(string name, byte[] content, string cacheControl, CancellationToken ct,
        string contentType = "application/javascript; charset=utf-8")
    {
        var s = Settings();
        await RunAsync($"upload {name}", async () =>
        {
            await EnsureBucketAsync(s.WidgetsBucket, publicWidgets: true, ct);
            using var stream = new MemoryStream(content);
            var request = new PutObjectRequest
            {
                BucketName = s.WidgetsBucket,
                Key = name,
                InputStream = stream,
                ContentType = contentType,
                UseChunkEncoding = false,
            };
            request.Headers.CacheControl = cacheControl;
            await client!.Value.PutObjectAsync(request, ct);
        });
    }

    public async Task DeleteWidgetFileAsync(string name, CancellationToken ct)
    {
        var s = Settings();
        await RunAsync($"delete {name}", () =>
            client!.Value.DeleteObjectAsync(new DeleteObjectRequest { BucketName = s.WidgetsBucket, Key = name }, ct));
    }

    public async Task PutAttachmentAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        var s = Settings();
        await RunAsync("upload an attachment", async () =>
        {
            await EnsureBucketAsync(s.AttachmentsBucket, publicWidgets: false, ct);
            using var stream = new MemoryStream(content);
            await client!.Value.PutObjectAsync(new PutObjectRequest
            {
                BucketName = s.AttachmentsBucket,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                UseChunkEncoding = false,
            }, ct);
        });
    }

    public async Task<byte[]> ReadAttachmentAsync(string key, CancellationToken ct)
    {
        var s = Settings();
        byte[] result = [];
        await RunAsync("read an attachment", async () =>
        {
            using var response = await client!.Value.GetObjectAsync(s.AttachmentsBucket, key, ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            result = buffer.ToArray();
        });
        return result;
    }

    public string? AttachmentUrl(string key, string fileName, string contentType)
    {
        if (settings is null || signer is null)
            return null;
        // Картинки открываются в браузере, остальное скачивается с исходным именем.
        var disposition = AttachmentPolicy.IsInlineImage(contentType) ? "inline" : "attachment";
        return signer.Value.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = settings.AttachmentsBucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(settings.PresignedUrlLifetime),
            Protocol = settings.PublicUrl.Scheme == Uri.UriSchemeHttps ? Protocol.HTTPS : Protocol.HTTP,
            ResponseHeaderOverrides =
            {
                ContentType = contentType,
                ContentDisposition = $"{disposition}; filename*=UTF-8''{Uri.EscapeDataString(fileName)}",
            },
        });
    }

    private StorageSettings Settings() =>
        settings ?? throw new DomainException(ErrorCodes.StorageNotConfigured, "File storage (S3) is not configured");

    private async Task RunAsync(string action, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            logger.LogError(ex, "S3: could not {Action}", action);
            throw new DomainException(ErrorCodes.StorageUnavailable, $"File storage (S3) is unavailable: could not {action}");
        }
    }

    private async Task EnsureBucketAsync(string bucket, bool publicWidgets, CancellationToken ct)
    {
        if (readyBuckets.ContainsKey(bucket))
            return;
        await bucketLock.WaitAsync(ct);
        try
        {
            if (readyBuckets.ContainsKey(bucket))
                return;
            var s3 = client!.Value;
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, bucket))
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, ct);
                logger.LogInformation("Created S3 bucket {Bucket}", bucket);
            }
            if (publicWidgets)
                await AllowPublicWidgetReadsAsync(s3, bucket, ct);
            readyBuckets[bucket] = true;
        }
        finally
        {
            bucketLock.Release();
        }
    }

    /// <summary>Анонимное чтение только файлов виджетов (скрипты и картинки кнопок widget_*).</summary>
    private async Task AllowPublicWidgetReadsAsync(AmazonS3Client s3, string bucket, CancellationToken ct)
    {
        try
        {
            await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest
            {
                BucketName = bucket,
                Policy = $$"""
                    {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"AWS":["*"]},
                    "Action":["s3:GetObject"],"Resource":["arn:aws:s3:::{{bucket}}/widget_*"]}]}
                    """,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotImplemented)
        {
            // Политику может задавать только администратор хранилища: тогда её настраивают вручную (docs/deployment.md).
            logger.LogWarning("Could not set public-read policy on bucket {Bucket}: {Error}", bucket, ex.Message);
        }
    }

    private static AmazonS3Client Create(StorageSettings s, Uri endpoint) =>
        new(new BasicAWSCredentials(s.AccessKey, s.SecretKey), new AmazonS3Config
        {
            ServiceURL = endpoint.ToString(),
            AuthenticationRegion = s.Region,
            ForcePathStyle = s.ForcePathStyle,
            // MinIO и другие S3-совместимые хранилища не всегда понимают новые контрольные суммы AWS SDK.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public void Dispose()
    {
        if (client is { IsValueCreated: true })
            client.Value.Dispose();
        if (signer is { IsValueCreated: true })
            signer.Value.Dispose();
        bucketLock.Dispose();
    }
}
