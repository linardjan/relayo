using Relayo.Core;
using Relayo.Core.Clients;

namespace Relayo.Internal.Api;

internal static class ClientEndpoints
{
    public static RouteGroupBuilder MapClientEndpoints(this RouteGroupBuilder api)
    {
        // Карточку клиента ведут все сотрудники (как в Usedesk), удаляет — администратор.
        var clients = api.MapGroup("/clients").RequireAuthorization();

        clients.MapGet("/", (ClientService service, string? search, Guid? siteId, bool? anonymous, int? page,
                int? pageSize, CancellationToken ct) =>
            service.SearchAsync(search, siteId, anonymous == true, page ?? 1, pageSize ?? 20, ct));

        clients.MapGet("/sites", (ClientService service, CancellationToken ct) => service.SitesAsync(ct));

        clients.MapGet("/{id:guid}", (Guid id, ClientService service, CancellationToken ct) =>
            service.GetAsync(id, ct));

        clients.MapPost("/", (NewClientInput input, ClientService service, CancellationToken ct) =>
            service.CreateAsync(input, ct));

        clients.MapPut("/{id:guid}", (Guid id, ClientProfileInput input, ClientService service, CancellationToken ct) =>
            service.UpdateAsync(id, input, ct));

        clients.MapPost("/{id:guid}/avatar", async (Guid id, HttpRequest request, ClientService service, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                throw new DomainException(ErrorCodes.InvalidInput, "Send the image as multipart/form-data");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new DomainException(ErrorCodes.InvalidInput, "Choose an image");
            var upload = (await FormFiles.ReadAsync(new FormFileCollection { file }, ClientService.AvatarMaxBytes, ct))[0];
            return await service.SetAvatarAsync(id, upload, ct);
        });

        clients.MapDelete("/{id:guid}/avatar", (Guid id, ClientService service, CancellationToken ct) =>
            service.RemoveAvatarAsync(id, ct));

        clients.MapDelete("/{id:guid}", async (Guid id, ClientService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization(SettingsEndpoints.AdminPolicy);

        return api;
    }
}
