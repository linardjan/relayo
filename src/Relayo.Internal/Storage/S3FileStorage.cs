using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Relayo.Core;
using Relayo.Core.Sites;

namespace Relayo.Internal.Storage;

/// <summary>Подключение к S3 (MinIO компании; локально RustFS). Настраивается в .env: это инфраструктура, как БД.</summary>
public sealed record StorageSettings(
    Uri Endpoint,
    string Region,
    string AccessKey,
    string SecretKey,
    bool ForcePathStyle,
    string WidgetsBucket);

/// <summary>
/// Файлы в S3. Бакет виджетов создаётся при первой записи; читать файлы виджетов (widget_*) разрешено всем —
/// их загружают браузеры посетителей. Остальное в бакете закрыто.
/// </summary>
public sealed class S3FileStorage(StorageSettings? settings, ILogger<S3FileStorage> logger) : IFileStorage, IDisposable
{
    private readonly Lazy<AmazonS3Client>? client = settings is { } s ? new(() => Create(s)) : null;
    private readonly SemaphoreSlim bucketLock = new(1, 1);
    private bool bucketReady;

    public bool IsConfigured => settings is not null;

    public async Task PutWidgetFileAsync(string name, byte[] content, string cacheControl, CancellationToken ct)
    {
        if (settings is null || client is null)
            throw new DomainException(ErrorCodes.StorageNotConfigured, "File storage (S3) is not configured");
        try
        {
            await EnsureWidgetsBucketAsync(ct);
            using var stream = new MemoryStream(content);
            var request = new PutObjectRequest
            {
                BucketName = settings.WidgetsBucket,
                Key = name,
                InputStream = stream,
                ContentType = "application/javascript; charset=utf-8",
                UseChunkEncoding = false,
            };
            request.Headers.CacheControl = cacheControl;
            await client.Value.PutObjectAsync(request, ct);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            logger.LogError(ex, "Could not upload {File} to S3", name);
            throw new DomainException(ErrorCodes.StorageUnavailable, $"Could not upload the widget file to S3: {ex.Message}");
        }
    }

    private async Task EnsureWidgetsBucketAsync(CancellationToken ct)
    {
        if (bucketReady)
            return;
        await bucketLock.WaitAsync(ct);
        try
        {
            if (bucketReady)
                return;
            var s3 = client!.Value;
            var bucket = settings!.WidgetsBucket;
            if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, bucket))
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, ct);
                logger.LogInformation("Created S3 bucket {Bucket}", bucket);
            }
            try
            {
                // Анонимное чтение только файлов виджетов.
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
            bucketReady = true;
        }
        finally
        {
            bucketLock.Release();
        }
    }

    private static AmazonS3Client Create(StorageSettings s) =>
        new(new BasicAWSCredentials(s.AccessKey, s.SecretKey), new AmazonS3Config
        {
            ServiceURL = s.Endpoint.ToString(),
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
        bucketLock.Dispose();
    }
}
