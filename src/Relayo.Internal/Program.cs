using System.Threading.RateLimiting;
using JasperFx;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Relayo.Contracts;
using Relayo.Core;
using Relayo.Core.Auth;
using Relayo.Core.Data;
using Relayo.Internal.Api;
using Relayo.Internal.Configuration;
using Relayo.Internal.Grpc;
using Relayo.Internal.Hosting;
using Relayo.ServiceDefaults;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;
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
builder.Services.AddRelayoCore(settings.Jwt, settings.Database);
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
});

// Вход в админку
var tokenService = new TokenService(settings.Jwt, TimeProvider.System);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = tokenService.ValidationParameters;
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(null);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// gRPC для публичного сервиса
builder.Services.AddSingleton<InternalKeyInterceptor>();
builder.Services.AddGrpc(o => o.Interceptors.Add<InternalKeyInterceptor>());

var app = builder.Build();

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
    .RequireLocalPort(settings.HttpPort);

// gRPC — только на своём порту
app.MapGrpcService<InternalGatewayService>()
    .RequireLocalPort(settings.GrpcPort);

return await app.RunJasperFxCommands(args);
