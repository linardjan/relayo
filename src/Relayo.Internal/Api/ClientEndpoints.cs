using Relayo.Core.Clients;

namespace Relayo.Internal.Api;

internal static class ClientEndpoints
{
    public static RouteGroupBuilder MapClientEndpoints(this RouteGroupBuilder api)
    {
        var clients = api.MapGroup("/clients").RequireAuthorization();

        clients.MapGet("/", (ClientService service, string? search, Guid? siteId, bool? anonymous, int? page,
                int? pageSize, CancellationToken ct) =>
            service.SearchAsync(search, siteId, anonymous == true, page ?? 1, pageSize ?? 20, ct));

        clients.MapGet("/{id:guid}", (Guid id, ClientService service, CancellationToken ct) =>
            service.GetAsync(id, ct));

        return api;
    }
}
