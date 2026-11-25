using Microsoft.Extensions.Diagnostics.HealthChecks;
using Relayo.Contracts.Grpc;

namespace Relayo.Public.Internal;

/// <summary>Публичный сервис готов, только если отвечает внутренний.</summary>
internal sealed class InternalGatewayHealthCheck(InternalGateway.InternalGatewayClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await client.PingAsync(new PingRequest { Caller = "relayo-public" },
                deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Internal service is unavailable", ex);
        }
    }
}
