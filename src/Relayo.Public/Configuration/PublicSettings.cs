using Relayo.ServiceDefaults;

namespace Relayo.Public.Configuration;

public sealed record PublicSettings(
    int HttpPort,
    Uri InternalGrpcUrl,
    string InternalGrpcKey,
    RabbitSettings Rabbit,
    string[] CorsExtraOrigins,
    int RateLimitRequestsPerMinute)
{
    public static PublicSettings Load(IConfiguration configuration)
    {
        var config = new RelayoConfig(configuration);

        var grpcUrl = config.Required("INTERNAL_GRPC_URL");
        if (grpcUrl.Length > 0 && !Uri.IsWellFormedUriString(grpcUrl, UriKind.Absolute))
            config.AddError($"INTERNAL_GRPC_URL must be an absolute URL, got '{grpcUrl}'");

        var settings = new PublicSettings(
            config.Int("PUBLIC_HTTP_PORT", 5200),
            Uri.TryCreate(grpcUrl, UriKind.Absolute, out var uri) ? uri : new Uri("http://invalid"),
            config.Required("INTERNAL_GRPC_KEY"),
            RabbitSettings.Load(config, "RABBITMQ_PUBLIC_USER", "RABBITMQ_PUBLIC_PASSWORD"),
            config.List("PUBLIC_CORS_EXTRA_ORIGINS"),
            config.Int("PUBLIC_RATE_LIMIT_REQUESTS_PER_MINUTE", 120));

        config.ThrowIfInvalid();
        return settings;
    }
}
