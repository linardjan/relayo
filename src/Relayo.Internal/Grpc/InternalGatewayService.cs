using Grpc.Core;
using Relayo.Contracts.Grpc;

namespace Relayo.Internal.Grpc;

internal sealed class InternalGatewayService(TimeProvider time) : InternalGateway.InternalGatewayBase
{
    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context) =>
        Task.FromResult(new PingReply
        {
            Service = "relayo-internal",
            ServerTimeUnixMs = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
}
