using Microsoft.Extensions.Configuration;
using Npgsql;
using Relayo.Core.Auth;
using Relayo.Core.Data;
using Relayo.ServiceDefaults;

namespace Relayo.Internal.Configuration;

public sealed record InternalSettings(
    int HttpPort,
    int GrpcPort,
    string GrpcKey,
    string PostgresConnectionString,
    RabbitSettings Rabbit,
    JwtOptions Jwt,
    DatabaseInitializerOptions Database,
    bool SecureCookies)
{
    public static InternalSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        var config = new RelayoConfig(configuration);

        var postgres = new NpgsqlConnectionStringBuilder
        {
            Host = config.Required("POSTGRES_HOST"),
            Port = config.Int("POSTGRES_PORT", 5432),
            Database = config.Required("POSTGRES_DB"),
            Username = config.Required("POSTGRES_USER"),
            Password = config.Required("POSTGRES_PASSWORD"),
        }.ConnectionString;

        var jwt = new JwtOptions(
            config.Get("JWT_ISSUER", "relayo"),
            config.Get("JWT_AUDIENCE", "relayo-admin"),
            config.Base64Key("JWT_SIGNING_KEY", minBytes: 32),
            TimeSpan.FromMinutes(config.Int("JWT_ACCESS_TOKEN_MINUTES", 15)),
            TimeSpan.FromDays(config.Int("JWT_REFRESH_TOKEN_DAYS", 14)));

        var grpcKey = config.Required("INTERNAL_GRPC_KEY");
        if (grpcKey.Length is > 0 and < 32)
            config.AddError("INTERNAL_GRPC_KEY must be at least 32 characters (generate with: openssl rand -base64 32)");

        var settings = new InternalSettings(
            config.Int("INTERNAL_HTTP_PORT", 5100),
            config.Int("INTERNAL_GRPC_PORT", 5101),
            grpcKey,
            postgres,
            RabbitSettings.Load(config, "RABBITMQ_USER", "RABBITMQ_PASSWORD"),
            jwt,
            new DatabaseInitializerOptions(
                postgres,
                config.Bool("DB_MIGRATE_ON_START", true),
                config.Optional("BOOTSTRAP_ADMIN_LOGIN"),
                config.Optional("BOOTSTRAP_ADMIN_PASSWORD")),
            SecureCookies: !isDevelopment);

        config.ThrowIfInvalid();
        return settings;
    }
}
