using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using JasperFx;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Relayo.Contracts;
using Relayo.Contracts.Messages;
using Relayo.Core;
using Relayo.Core.Auth;
using Relayo.Core.Data;
using System.Net;
using Relayo.Internal.Api;
using Relayo.Internal.Automation;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Configuration;
using Relayo.Internal.Grpc;
using Relayo.Internal.Hosting;
using Relayo.Internal.Messaging;
using Relayo.Internal.Realtime;
using Relayo.Internal.Storage;
using Relayo.Core.Sites;
using Relayo.ServiceDefaults;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelayoDefaults("relayo-internal");

var settings = InternalSettings.Load(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(settings);

// Два порта: HTTP для админки и отдельный HTTP/2 для gRPC публичного сервиса.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(settings.HttpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    kestrel.ListenAnyIP(settings.GrpcPort, o => o.Protocols = HttpProtocols.Http2);
});

// Данные
builder.Services.AddDbContextWithWolverineIntegration<RelayoDbContext>(
    db => db.UseRelayoPostgres(settings.PostgresConnectionString));
builder.Services.AddRelayoCore(settings.Jwt, settings.Database, settings.SecretsEncryptionKey, settings.ClientTokens);
builder.Services.AddScoped<WidgetEvents>();
builder.Services.AddScoped<ConversationEvents>();
builder.Services.AddScoped<AutomationEvents>();

// Файлы виджетов в S3 (п. 3.5 ТЗ)
builder.Services.AddSingleton(sp => new S3FileStorage(settings.Storage, sp.GetRequiredService<ILogger<S3FileStorage>>()));
builder.Services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<S3FileStorage>());
builder.Services.AddSingleton(new WidgetPublishingOptions(settings.PublicBaseUrl, settings.WidgetFilesBaseUrl));
builder.Services.AddHostedService<TestWidgetsPublisher>();
builder.Services.AddSingleton<RuleRunner>();

// Telegram: Bot API вызывается из внутреннего сервиса, токены ботов наружу не выходят.
// Нужен исходящий доступ к api.telegram.org (напрямую или через TELEGRAM_PROXY_URL).
builder.Services.AddHttpClient(TelegramBotApi.HttpClientName, http =>
    {
        http.BaseAddress = new Uri(settings.Telegram.ApiBaseUrl.ToString().TrimEnd('/') + "/");
        // Long polling держит запрос до 25 секунд.
        http.Timeout = TimeSpan.FromSeconds(TelegramBotApi.PollTimeoutSeconds + 15);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        Proxy = settings.Telegram.ProxyUrl is { } proxy ? new WebProxy(proxy) : null,
        UseProxy = settings.Telegram.ProxyUrl is not null,
    })
    // В адресе запроса токен бота: стандартные логи HttpClient его бы записали.
    .RemoveAllLoggers();
builder.Services.AddSingleton<TelegramBotApi>();
builder.Services.AddSingleton<TelegramBotTokens>();
builder.Services.AddSingleton<TelegramRateLimiter>();
builder.Services.AddScoped<TelegramOutbox>();
builder.Services.AddScoped<TelegramUpdateProcessor>();
builder.Services.AddScoped<TelegramBotSettingsService>();
builder.Services.AddHostedService<TelegramUpdatesService>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RelayoDbContext>("postgres", tags: [ServiceDefaultsExtensions.ReadyTag]);

// Шина: RabbitMQ + outbox и inbox в PostgreSQL (схема wolverine)
builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "relayo-internal";
    opts.PersistMessagesWithPostgresql(settings.PostgresConnectionString, "wolverine");
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    opts.Policies.UseDurableInboxOnAllListeners();

    opts.UseRabbitMq(f =>
        {
            f.HostName = settings.Rabbit.Host;
            f.Port = settings.Rabbit.Port;
            f.VirtualHost = settings.Rabbit.VirtualHost;
            f.UserName = settings.Rabbit.User;
            f.Password = settings.Rabbit.Password;
        })
        .AutoProvision();
    opts.ListenToRabbitQueue(Queues.Internal);

    // События для виджетов уходят во все экземпляры публичного сервиса.
    opts.PublishMessage<WidgetMessagePublished>().ToRabbitExchange(Queues.WidgetEvents,
        exchange => exchange.ExchangeType = ExchangeType.Fanout);

    // Исходящие в Telegram: durable-очередь в PostgreSQL, лимиты Telegram соблюдает обработчик.
    opts.PublishMessage<SendTelegramMessage>().ToLocalQueue(TelegramOutbox.QueueName);
    opts.LocalQueue(TelegramOutbox.QueueName).UseDurableInbox().MaximumParallelMessages(16);

    // Правила: события и таймеры «не ответил N минут» (отложенные сообщения хранятся в PostgreSQL).
    opts.PublishMessage<RuleEventOccurred>().ToLocalQueue(AutomationEvents.QueueName);
    opts.PublishMessage<RuleTimerElapsed>().ToLocalQueue(AutomationEvents.QueueName);
    opts.LocalQueue(AutomationEvents.QueueName).UseDurableInbox().MaximumParallelMessages(4);
    opts.OnException<HttpRequestException>().Or<TaskCanceledException>().Or<TelegramApiException>()
        .RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
});

// Вход в админку
var tokenService = new TokenService(settings.Jwt, TimeProvider.System);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = tokenService.ValidationParameters;
        // Браузер не может передать заголовок при подключении WebSocket: токен приходит в query.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.HttpContext.Request.Path.StartsWithSegments(OperatorHub.Path)
                    && ctx.Request.Query.TryGetValue("access_token", out var token))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(null)
    .AddPolicy(SettingsEndpoints.AdminPolicy, p => p.RequireRole(nameof(Relayo.Core.Users.UserRole.Admin)));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Realtime для админки
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<OperatorNotifier>();

// gRPC для публичного сервиса
builder.Services.AddSingleton<InternalKeyInterceptor>();
builder.Services.AddGrpc(o => o.Interceptors.Add<InternalKeyInterceptor>());

var app = builder.Build();

// За reverse proxy: адрес клиента и схема из X-Forwarded-* (только от TRUSTED_PROXIES).
app.UseRelayoForwardedHeaders();
app.UseSerilogRequestLogging(o => o.GetLevel = (http, _, ex) =>
    ex is not null || http.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : http.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
    : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseLocalPortRestriction();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRelayoHealth();

// API админки — только на HTTP-порту
app.MapGroup("/api")
    .MapAuthEndpoints()
    .MapTicketEndpoints()
    .MapClientEndpoints()
    .MapSettingsEndpoints()
    .MapAutomationEndpoints()
    .RequireLocalPort(settings.HttpPort);
app.MapHub<OperatorHub>(OperatorHub.Path)
    .RequireLocalPort(settings.HttpPort);

// gRPC — только на своём порту
app.MapGrpcService<InternalGatewayService>()
    .RequireLocalPort(settings.GrpcPort);

return await app.RunJasperFxCommands(args);
