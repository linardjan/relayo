using Relayo.Internal.Usedesk;

namespace Relayo.Internal.Api;

/// <summary>Временный импорт из Usedesk: подключение к его базе, предпросмотр и перенос настроек. Только администратор.</summary>
internal static class UsedeskEndpoints
{
    public static RouteGroupBuilder MapUsedeskEndpoints(this RouteGroupBuilder settings)
    {
        var usedesk = settings.MapGroup("/usedesk");
        usedesk.MapGet("/connection", async (UsedeskImportService service, CancellationToken ct) =>
            await service.GetConnectionAsync(ct) is { } view ? Results.Ok(view) : Results.NoContent());
        usedesk.MapPut("/connection", (UsedeskConnectionInput input, UsedeskImportService service, CancellationToken ct) =>
            service.SaveConnectionAsync(input, ct));
        usedesk.MapPost("/connection/check", (UsedeskImportService service, CancellationToken ct) => service.CheckAsync(ct));
        usedesk.MapDelete("/connection", async (UsedeskImportService service, CancellationToken ct) =>
        {
            await service.ForgetAsync(ct);
            return Results.NoContent();
        });
        usedesk.MapGet("/preview", (UsedeskImportService service, CancellationToken ct) => service.PreviewAsync(ct));
        usedesk.MapPost("/import", (UsedeskImportRequest request, UsedeskImportService service, CancellationToken ct) =>
            service.ImportAsync(request, ct));
        return settings;
    }
}
