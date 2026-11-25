using Microsoft.Extensions.Caching.Memory;
using Relayo.Contracts.Grpc;
using Relayo.Public.Configuration;
using Relayo.Public.Internal;

namespace Relayo.Public.Widget;

/// <summary>
/// С каких доменов можно обращаться к API виджета.
/// Для CORS — домены всех сайтов (обновляются раз в минуту), для конкретного виджета —
/// только домены его сайта. Плюс PUBLIC_CORS_EXTRA_ORIGINS для разработки.
/// </summary>
public sealed class OriginPolicy(PublicSettings settings)
{
    private readonly HashSet<string> _extra = settings.CorsExtraOrigins.Select(Normalize).OfType<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private volatile HashSet<string> _allSites = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAllowedForCors(string origin) =>
        Normalize(origin) is { } o && (_extra.Contains(o) || _allSites.Contains(o));

    public bool IsAllowedForWidget(string? origin, WidgetConfig widget) =>
        Normalize(origin) is { } o
        && (_extra.Contains(o) || widget.AllowedOrigins.Contains(o, StringComparer.OrdinalIgnoreCase));

    public void Update(IEnumerable<string> origins) =>
        _allSites = origins.Select(Normalize).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string? origin) =>
        Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
            : null;
}

/// <summary>Раз в минуту забирает у внутреннего сервиса домены сайтов.</summary>
internal sealed class OriginRefresher(OriginPolicy policy, IServiceScopeFactory scopes, ILogger<OriginRefresher> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var origins = await scope.ServiceProvider.GetRequiredService<InternalApi>().GetAllowedOriginsAsync(ct);
                policy.Update(origins.Origins);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh allowed origins, keeping the previous list");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Настройки виджетов кэшируются на 30 секунд: они нужны на каждый запрос.</summary>
public sealed class WidgetConfigCache(InternalApi api, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<WidgetConfig> GetAsync(string widgetId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"widget:{widgetId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return api.GetWidgetAsync(widgetId, ct);
        }))!;
}
