using System.Security.Claims;
using Relayo.Core.Auth;
using Relayo.Core.Automation;

namespace Relayo.Internal.Api;

/// <summary>Раздел «Automation → Rules»: только администратор.</summary>
internal static class AutomationEndpoints
{
    public static RouteGroupBuilder MapAutomationEndpoints(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/automation/rules").RequireAuthorization(SettingsEndpoints.AdminPolicy);

        rules.MapGet("/", (RuleService service, CancellationToken ct) => service.ListAsync(ct));
        // Журнал срабатываний («Export → Download log»)
        rules.MapGet("/log", (int? days, RuleService service, CancellationToken ct) => service.ExecutionsAsync(days ?? 7, ct));
        rules.MapGet("/{id:guid}", (Guid id, RuleService service, CancellationToken ct) => service.GetAsync(id, ct));
        rules.MapPost("/", (RuleInput input, ClaimsPrincipal user, RuleService service, CancellationToken ct) =>
            service.CreateAsync(input, UserId(user), ct));
        rules.MapPut("/{id:guid}", (Guid id, RuleInput input, ClaimsPrincipal user, RuleService service, CancellationToken ct) =>
            service.UpdateAsync(id, input, UserId(user), ct));
        rules.MapDelete("/{id:guid}", async (Guid id, RuleService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        // Порядок применения: id правил сверху вниз.
        rules.MapPut("/order", (IReadOnlyList<Guid> order, RuleService service, CancellationToken ct) =>
            service.ReorderAsync(order, ct));

        return api;
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(TokenService.SubjectClaim)!);
}
