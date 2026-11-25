using System.Net;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace Relayo.ServiceDefaults;

/// <summary>
/// Работа за reverse proxy (nginx, балансировщик). Без этого сервис видит адрес прокси вместо адреса клиента,
/// и лимиты «на IP» превращаются в один общий лимит на всех.
/// Доверяем X-Forwarded-For и X-Forwarded-Proto только от адресов из TRUSTED_PROXIES.
/// </summary>
public static class ForwardedHeaders
{
    public const string ConfigKey = "TRUSTED_PROXIES";

    public static WebApplication UseRelayoForwardedHeaders(this WebApplication app)
    {
        var networks = Parse(app.Configuration[ConfigKey]);
        if (networks.Count == 0)
            return app;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                               | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            // Цепочка прокси (балансировщик → nginx): разбираем, пока адреса доверенные.
            ForwardLimit = null,
        };
        foreach (var network in networks)
            options.KnownIPNetworks.Add(network);
        app.UseForwardedHeaders(options);
        return app;
    }

    /// <summary>Список через запятую: 10.0.0.5, 172.30.0.0/24. Одиночный адрес — сеть из одного адреса.</summary>
    internal static List<IPNetwork> Parse(string? value)
    {
        var result = new List<IPNetwork>();
        foreach (var raw in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(raw, out var network))
                result.Add(network);
            else if (IPAddress.TryParse(raw, out var address))
                result.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else
                throw new InvalidOperationException($"{ConfigKey}: '{raw}' is not an IP address or a network like 10.0.0.0/24");
        }
        return result;
    }
}
