using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Relayo.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>
    /// Общая настройка обоих сервисов: .env, логи (консоль + Seq),
    /// трассировка (OpenTelemetry → Seq), health checks, ProblemDetails.
    /// </summary>
    public static WebApplicationBuilder AddRelayoDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Configuration.AddDotEnvFile();
        var config = new RelayoConfig(builder.Configuration);

        var seqUrl = config.Optional("SEQ_URL");
        var seqApiKey = config.Optional("SEQ_API_KEY");
        builder.Services.AddSerilog(log =>
        {
            log.MinimumLevel.Information()
               .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
               .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
               .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
               .Enrich.FromLogContext()
               .Enrich.WithProperty("Service", serviceName)
               .WriteTo.Console(outputTemplate:
                   "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
            if (seqUrl is not null)
                log.WriteTo.Seq(seqUrl, apiKey: seqApiKey);
        });

        var otlpEndpoint = config.Optional("OTEL_EXPORTER_OTLP_ENDPOINT");
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddSource("Wolverine")
                    .AddProcessor(new SecretRedactingProcessor());
                if (otlpEndpoint is not null)
                    tracing.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint.TrimEnd('/') + "/v1/traces");
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
            });

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag]);
        builder.Services.AddProblemDetails();

        return builder;
    }

    /// <summary>
    /// /health/live — процесс жив; /health/ready — сервис готов принимать запросы
    /// (доступны БД, внутренний сервис и т.д.).
    /// </summary>
    public static WebApplication MapRelayoHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(LiveTag),
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(LiveTag) || r.Tags.Contains(ReadyTag),
        });
        return app;
    }
}
