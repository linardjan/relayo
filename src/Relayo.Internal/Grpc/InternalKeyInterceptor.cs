using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Relayo.Contracts;
using Relayo.Internal.Configuration;

namespace Relayo.Internal.Grpc;

/// <summary>Отклоняет вызовы gRPC без правильного общего ключа.</summary>
internal sealed class InternalKeyInterceptor(InternalSettings settings) : Interceptor
{
    private readonly byte[] _expected = Encoding.UTF8.GetBytes(settings.GrpcKey);

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var provided = context.RequestHeaders.GetValue(GrpcHeaders.InternalKey);
        if (provided is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), _expected))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid internal key"));
        }
    }
}
