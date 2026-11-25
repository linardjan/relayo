using System.Threading.RateLimiting;
using JasperFx;
using Relayo.Contracts;
using Relayo.Contracts.Grpc;
using Relayo.Contracts.Messages;
using Relayo.Public.Configuration;
using Relayo.Public.Internal;
using Relayo.ServiceDefaults;
using Serilog;
using Wolverine;
using Wolverine.RabbitMQ;

// Публичный сервис: всё, к чему обращаются из интернета.
// Доступа к БД нет: данные только через gRPC внутреннего сервиса и RabbitMQ (п. 2.2 ТЗ).

var builder = WebApplication.CreateBuilder(args);
builder.AddRelayoDefaults("relayo-public");

var settings = PublicSettings.Load(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);

builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(settings.HttpPort));

// gRPC-клиент внутреннего сервиса с общим ключом в каждом вызове
builder.Services
    .AddGrpcClient<InternalGateway.InternalGatewayClient>(o => o.Address = settings.InternalGrpcUrl)
    .AddCallCredentials((_, metadata) =>
    {
        metadata.Add(GrpcHeaders.InternalKey, settings.InternalGrpcKey);
        return Task.CompletedTask;
    })
    // Внутри сети вызовы идут без TLS; сетевую изоляцию обеспечивает ИБ.
    .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = settings.InternalGrpcUrl.Scheme == "http");
builder.Services.AddHealthChecks()
    .AddCheck<InternalGatewayHealthCheck>("internal-gateway", tags: [ServiceDefaultsExtensions.ReadyTag]);

// Шина: только RabbitMQ, без хранилища в БД
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "relayo-public";
    opts.UseRabbitMq(f =>
        {
            f.HostName = settings.Rabbit.Host;
            f.Port = settings.Rabbit.Port;
            f.VirtualHost = settings.Rabbit.VirtualHost;
            f.UserName = settings.Rabbit.User;
            f.Password = settings.Rabbit.Password;
        })
        .AutoProvision();
    opts.PublishMessage<PublicServiceStarted>().ToRabbitQueue(Queues.Internal);
    opts.ListenToRabbitQueue(Queues.Public);
});
builder.Services.AddHostedService<StartupAnnouncer>();

// CORS: домены сайтов и зеркал появятся из админки, пока только дополнительные из .env
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(settings.CorsExtraOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Ограничение частоты запросов с одного адреса
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        http.Request.Path.StartsWithSegments("/health")
            ? RateLimitPartition.GetNoLimiter("health")
            : RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.RateLimitRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
});

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

app.MapRelayoHealth();

var widget = app.MapGroup("/api/widget");

// Временный эндпоинт для проверки связки «публичный → внутренний» по gRPC.
if (app.Environment.IsDevelopment())
{
    widget.MapGet("/ping", async (InternalGateway.InternalGatewayClient client, CancellationToken ct) =>
    {
        var reply = await client.PingAsync(new PingRequest { Caller = "relayo-public" }, cancellationToken: ct);
        return Results.Ok(new
        {
            service = "relayo-public",
            internalService = reply.Service,
            internalTime = DateTimeOffset.FromUnixTimeMilliseconds(reply.ServerTimeUnixMs),
        });
    });
}

return await app.RunJasperFxCommands(args);
