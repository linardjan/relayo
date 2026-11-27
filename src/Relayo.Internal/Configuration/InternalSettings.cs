using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Relayo.Core.Auth;
using Relayo.Core.Data;
using Relayo.Core.Files;
using Relayo.Core.Sites;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Channels.WhatsApp;
using Relayo.Internal.Storage;
using Relayo.ServiceDefaults;
using Relayo.ServiceDefaults.Redis;

namespace Relayo.Internal.Configuration;

public sealed record InternalSettings(
    int HttpPort,
    int GrpcPort,
    string GrpcKey,
    string DbConnectionString,
    RabbitSettings Rabbit,
    JwtOptions Jwt,
    DatabaseInitializerOptions Database,
    byte[] SecretsEncryptionKey,
    ClientTokenOptions ClientTokens,
    string PublicBaseUrl,
    bool SecureCookies,
    TelegramSettings Telegram,
    StorageSettings? Storage,
    string? WidgetFilesBaseUrl,
    AttachmentLimits Attachments,
    RedisSettings? Redis,
    WhatsAppGatewaySettings? WhatsAppGateway = null)
{
    public static InternalSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        var config = new RelayoConfig(configuration);

        var database = LoadDbConnectionString(config);

        var jwt = new JwtOptions(
            config.Get("JWT_ISSUER", "relayo"),
            config.Get("JWT_AUDIENCE", "relayo-admin"),
            config.Base64Key("JWT_SIGNING_KEY", minBytes: 32),
            TimeSpan.FromMinutes(config.Int("JWT_ACCESS_TOKEN_MINUTES", 15)),
            TimeSpan.FromDays(config.Int("JWT_REFRESH_TOKEN_DAYS", 14)));

        var grpcKey = config.Required("INTERNAL_GRPC_KEY");
        if (grpcKey.Length is > 0 and < 32)
            config.AddError("INTERNAL_GRPC_KEY must be at least 32 characters (generate with: openssl rand -base64 32)");

        var secretsKey = config.Base64Key("SECRETS_ENCRYPTION_KEY", minBytes: 32);
        if (secretsKey.Length > 32)
            secretsKey = secretsKey[..32];

        var testSite = isDevelopment && config.Bool("DEV_SEED_TEST_SITE", false)
            ? LoadTestSite(config)
            : null;

        var settings = new InternalSettings(
            config.Int("INTERNAL_HTTP_PORT", 5100),
            config.Int("INTERNAL_GRPC_PORT", 5101),
            grpcKey,
            database,
            RabbitSettings.Load(config, "RABBITMQ_USER", "RABBITMQ_PASSWORD"),
            jwt,
            new DatabaseInitializerOptions(
                database,
                config.Bool("DB_MIGRATE_ON_START", true),
                config.Optional("BOOTSTRAP_ADMIN_LOGIN"),
                config.Optional("BOOTSTRAP_ADMIN_PASSWORD"),
                testSite),
            secretsKey,
            new ClientTokenOptions(
                TimeSpan.FromSeconds(config.Int("CLIENT_TOKEN_CLOCK_SKEW_SECONDS", 60)),
                MaxLifetime: TimeSpan.FromHours(1)),
            config.Required("PUBLIC_BASE_URL"),
            SecureCookies: !isDevelopment,
            LoadTelegram(config),
            LoadStorage(config),
            config.Optional("WIDGET_FILES_BASE_URL"),
            LoadAttachments(config),
            // Без Redis — один экземпляр: realtime операторов, лимиты и опрос Telegram в пределах процесса.
            RedisSettings.Load(config),
            LoadWhatsAppGateway(config));

        config.ThrowIfInvalid();
        return settings;
    }

    /// <summary>
    /// Строка подключения к MS SQL. MSSQL_AUTH=sql (по умолчанию) — логин и пароль SQL Server;
    /// MSSQL_AUTH=windows — учётка Windows, под которой запущен процесс (Integrated Security, SSPI/Kerberos),
    /// MSSQL_USER и MSSQL_PASSWORD тогда не нужны и не используются.
    /// </summary>
    internal static string LoadDbConnectionString(RelayoConfig config)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{config.Required("MSSQL_HOST")},{config.Int("MSSQL_PORT", 1433)}",
            InitialCatalog = config.Required("MSSQL_DB"),
            // Шифрование соединения обязательно. Самоподписанный сертификат (локальный Docker) —
            // только с MSSQL_TRUST_SERVER_CERTIFICATE=true.
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = config.Bool("MSSQL_TRUST_SERVER_CERTIFICATE", false),
            ApplicationName = "relayo-internal",
        };

        var auth = config.Get("MSSQL_AUTH", "sql").ToLowerInvariant();
        switch (auth)
        {
            case "sql":
                builder.UserID = config.Required("MSSQL_USER");
                builder.Password = config.Required("MSSQL_PASSWORD");
                break;
            case "windows":
                builder.IntegratedSecurity = true;
                break;
            default:
                config.AddError($"MSSQL_AUTH must be sql or windows, got '{auth}'");
                break;
        }
        return builder.ConnectionString;
    }

    /// <summary>S3 необязателен: без него не работает публикация виджетов, остальное работает.</summary>
    private static StorageSettings? LoadStorage(RelayoConfig config)
    {
        var endpoint = config.Optional("S3_ENDPOINT");
        if (endpoint is null)
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            config.AddError($"S3_ENDPOINT must be an absolute URL, got '{endpoint}'");
            return null;
        }
        var publicUrl = config.Get("S3_PUBLIC_URL", endpoint);
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri))
        {
            config.AddError($"S3_PUBLIC_URL must be an absolute URL, got '{publicUrl}'");
            publicUri = uri;
        }
        var minutes = config.Int("S3_PRESIGNED_URL_MINUTES", 15);
        if (minutes is < 1 or > 7 * 24 * 60)
            config.AddError("S3_PRESIGNED_URL_MINUTES must be between 1 and 10080 (7 days)");
        return new StorageSettings(uri, config.Get("S3_REGION", "us-east-1"), config.Required("S3_ACCESS_KEY"),
            config.Required("S3_SECRET_KEY"), config.Bool("S3_FORCE_PATH_STYLE", true),
            config.Get("S3_BUCKET_WIDGETS", "relayo-widgets"),
            config.Get("S3_BUCKET_ATTACHMENTS", "relayo-attachments"),
            publicUri,
            TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 7 * 24 * 60)));
    }

    /// <summary>Ограничения на вложения. Те же значения проверяет и публичный сервис.</summary>
    internal static AttachmentLimits LoadAttachments(RelayoConfig config)
    {
        var megabytes = config.Int("ATTACHMENTS_MAX_SIZE_MB", 20);
        if (megabytes is < 1 or > 100)
            config.AddError("ATTACHMENTS_MAX_SIZE_MB must be between 1 and 100");
        var types = config.List("ATTACHMENTS_ALLOWED_TYPES").Select(t => t.Trim().ToLowerInvariant()).ToArray();
        if (types.Length == 0)
            types = DefaultAttachmentTypes;
        return new AttachmentLimits(Math.Clamp(megabytes, 1, 100) * 1024L * 1024L, types);
    }

    internal static readonly string[] DefaultAttachmentTypes =
    [
        "image/jpeg", "image/png", "image/gif", "image/webp", "application/pdf", "text/plain",
        "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "audio/mpeg", "audio/ogg", "audio/mp4",
    ];

    /// <summary>Шлюз WhatsApp необязателен: без него номера работают только в режиме «только ссылка».</summary>
    private static WhatsAppGatewaySettings? LoadWhatsAppGateway(RelayoConfig config)
    {
        var url = config.Optional("WHATSAPP_GATEWAY_URL");
        if (url is null)
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            config.AddError($"WHATSAPP_GATEWAY_URL must be an absolute http(s) URL, got '{url}'");
        var key = config.Required("WHATSAPP_GATEWAY_KEY");
        if (key.Length is > 0 and < 32)
            config.AddError("WHATSAPP_GATEWAY_KEY must be at least 32 characters (generate with: openssl rand -base64 32)");
        return new WhatsAppGatewaySettings(uri ?? new Uri("http://localhost"), key);
    }

    private static TelegramSettings LoadTelegram(RelayoConfig config)
    {
        var modeValue = config.Get("TELEGRAM_UPDATES_MODE", "webhook");
        if (!Enum.TryParse<TelegramUpdatesMode>(modeValue, ignoreCase: true, out var mode))
            config.AddError($"TELEGRAM_UPDATES_MODE must be webhook or polling, got '{modeValue}'");

        Uri? ParseUrl(string key, string? value)
        {
            if (value is null)
                return null;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return uri;
            config.AddError($"{key} must be an absolute URL, got '{value}'");
            return null;
        }

        var apiBase = ParseUrl("TELEGRAM_API_BASE_URL", config.Get("TELEGRAM_API_BASE_URL", "https://api.telegram.org"))
                      ?? new Uri("https://api.telegram.org");
        return new TelegramSettings(mode, apiBase, ParseUrl("TELEGRAM_PROXY_URL", config.Optional("TELEGRAM_PROXY_URL")));
    }

    /// <summary>Тестовый сайт для tools/TestSite (п. 2.3 ТЗ). Только Development.</summary>
    private static TestSiteSeed LoadTestSite(RelayoConfig config)
    {
        var origins = new List<string>
        {
            $"http://localhost:{config.Int("TEST_SITE_PORT", 5300)}",
            $"http://localhost:{config.Int("TEST_SITE_MIRROR_PORT", 5301)}",
        };
        // Страница в VS Code Live Server тоже считается доменом тестового сайта.
        origins.AddRange(config.List("PUBLIC_CORS_EXTRA_ORIGINS"));

        Guid ParseGuid(string key)
        {
            var value = config.Required(key);
            if (Guid.TryParse(value, out var id))
                return id;
            if (value.Length > 0)
                config.AddError($"{key} must be a GUID, got '{value}'");
            return Guid.Empty;
        }

        var secret = config.Required("TEST_SITE_SECRET");
        if (secret.Length is > 0 and < 32)
            config.AddError("TEST_SITE_SECRET must be at least 32 characters (generate with: openssl rand -base64 32)");

        return new TestSiteSeed(
            config.Get("TEST_SITE_PRODUCT", "test"),
            origins,
            config.Get("TEST_SITE_KEY_ID", "test-key-1"),
            secret,
            ParseGuid("TEST_WIDGET_TOKEN_MODE_ID"),
            ParseGuid("TEST_WIDGET_DIRECT_MODE_ID"),
            WidgetKey("TEST_WIDGET_TOKEN_MODE_KEY", "testtoken1"),
            WidgetKey("TEST_WIDGET_DIRECT_MODE_KEY", "testdirect"));

        string WidgetKey(string key, string fallback)
        {
            var value = config.Get(key, fallback);
            if (!WidgetKeys.IsValid(value))
                config.AddError($"{key} must be {Widget.PublicKeyLength} latin letters or digits, got '{value}'");
            return value;
        }
    }
}
