namespace Relayo.Internal.Hosting;

/// <summary>Эндпоинт доступен только на указанном порту сервиса.</summary>
internal sealed record LocalPortMetadata(int Port);

/// <summary>
/// Разводит API админки и gRPC по разным портам.
/// Проверяется порт, на который фактически пришло соединение, а не заголовок Host:
/// за прокси (Vite в разработке, reverse proxy в бою) Host указывает на прокси.
/// </summary>
internal static class LocalPortRestriction
{
    public static TBuilder RequireLocalPort<TBuilder>(this TBuilder builder, int port)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new LocalPortMetadata(port));
        return builder;
    }

    /// <summary>Подключать после маршрутизации, до авторизации.</summary>
    public static IApplicationBuilder UseLocalPortRestriction(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var required = context.GetEndpoint()?.Metadata.GetMetadata<LocalPortMetadata>();
            if (required is not null && context.Connection.LocalPort != required.Port)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });
}
