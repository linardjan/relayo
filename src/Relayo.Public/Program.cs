using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using JasperFx;
using Relayo.Contracts;
using Relayo.Contracts.Grpc;
using Relayo.Contracts.Messages;
using Relayo.Public.Configuration;
using Relayo.Public.Internal;
using Relayo.Public.Telegram;
using Relayo.Public.Widget;
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
    .ConfigureChannel(o =>
    {
        o.UnsafeUseInsecureChannelCallCredentials = settings.InternalGrpcUrl.Scheme == "http";
        // После перезапуска внутреннего сервиса переподключаемся быстро: по умолчанию пауза растёт до 2 минут,
        // и всё это время виджеты получали бы 503.
        o.InitialReconnectBackoff = TimeSpan.FromSeconds(1);
        o.MaxReconnectBackoff = TimeSpan.FromSeconds(5);
    });
builder.Services.AddScoped<InternalApi>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OriginPolicy>();
builder.Services.AddScoped<WidgetConfigCache>();
builder.Services.AddHostedService<OriginRefresher>();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks()
    .AddCheck<InternalGatewayHealthCheck>("internal-gateway", tags: [ServiceDefaultsExtensions.ReadyTag]);

// Шина: только RabbitMQ, без хранилища в БД.
// У каждого экземпляра своя временная очередь, привязанная к fanout-обменнику событий виджетов.
// Имя машины бывает любой длины (в контейнере это 12 символов id), поэтому обрезаем его, а не всю строку.
var machine = Environment.MachineName.ToLowerInvariant();
var instanceQueue = Queues.PublicInstanceQueue($"{machine[..Math.Min(machine.Length, 15)]}-{Guid.NewGuid():N}");
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
        .AutoProvision()
        .DeclareExchange(Queues.WidgetEvents, exchange =>
        {
            exchange.ExchangeType = ExchangeType.Fanout;
            exchange.BindQueue(instanceQueue);
        });
    opts.PublishMessage<PublicServiceStarted>().ToRabbitQueue(Queues.Internal);
    // RabbitMQ 4 не разрешает временные (non-durable) очереди без exclusive,
    // поэтому очередь durable, но удаляется, когда экземпляр отключается.
    opts.ListenToRabbitQueue(instanceQueue, queue =>
    {
        queue.IsDurable = true;
        queue.AutoDelete = true;
    });
});
builder.Services.AddHostedService<StartupAnnouncer>();

// CORS: домены всех сайтов и зеркал (обновляются из внутреннего сервиса) и PUBLIC_CORS_EXTRA_ORIGINS
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<OriginPolicy>((cors, origins) =>
    cors.AddDefaultPolicy(p => p
        .SetIsOriginAllowed(origins.IsAllowedForCors)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Cookie не используются: токен посетителя передаётся в заголовке или в query (SignalR).
        .DisallowCredentials()));

// Ограничение частоты запросов с одного адреса
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (http.Request.Path.StartsWithSegments("/health"))
            return RateLimitPartition.GetNoLimiter("health");
        // Webhook Telegram приходит с немногих адресов Telegram: обычный лимит на IP его бы душил.
        // Отдельный высокий лимит защищает внутренний сервис от перебора секрета.
        if (http.Request.Path.StartsWithSegments("/telegram"))
            return RateLimitPartition.GetFixedWindowLimiter("tg:" + ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 6000,
                Window = TimeSpan.FromMinutes(1),
            });
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.RateLimitRequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        });
    });
});

var app = builder.Build();

// За reverse proxy: адрес клиента и схема из X-Forwarded-* (только от TRUSTED_PROXIES).
app.UseRelayoForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();

app.MapRelayoHealth();

// Файлы виджета (собираются из web/widget). Позже их будет отдавать S3 (п. 3.5 ТЗ).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=60",
});

app.MapWidgetEndpoints();
app.MapTelegramWebhook();
app.MapHub<WidgetHub>(WidgetHub.Path);

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
