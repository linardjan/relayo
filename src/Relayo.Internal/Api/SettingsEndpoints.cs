using System.Security.Claims;
using Relayo.Core;
using Relayo.Core.Auth;
using Relayo.Core.Directory;
using Relayo.Core.Sites;
using Relayo.Core.Users;
using Relayo.Internal.Channels.Telegram;
using Relayo.Internal.Configuration;

namespace Relayo.Internal.Api;

public sealed record CreateSiteRequest(string Code, string Name, IReadOnlyList<string> AllowedOrigins);

public sealed record CreateWidgetRequest(
    Guid SiteId,
    string Name,
    IdentificationMode IdentificationMode,
    string Title,
    string Greeting,
    string AccentColor,
    WidgetPosition Position,
    string DefaultLocale,
    Guid? TelegramBotId,
    WidgetOptions? Options = null);

public sealed record SetPasswordRequest(string Password);

public sealed record SetStatusRequest(OperatorStatus Status);

public sealed record GroupRequest(string Name);

/// <summary>
/// Виджет с кодом вставки. FileUrl — адрес опубликованного файла в S3 (если хранилище настроено),
/// ButtonIconUrl — адрес своей картинки кнопки для предпросмотра.
/// </summary>
public sealed record WidgetSettingsDto(WidgetView Widget, string EmbedCode, string? FileUrl, string? ButtonIconUrl);

/// <summary>Раздел «Настройки»: только администратор (п. 3.1 ТЗ).</summary>
internal static class SettingsEndpoints
{
    public const string AdminPolicy = "admin";

    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings").RequireAuthorization(AdminPolicy);

        // Сайты и ключи
        settings.MapGet("/sites", (SiteSettingsService service, CancellationToken ct) => service.ListSitesAsync(ct));
        settings.MapPost("/sites", (CreateSiteRequest r, SiteSettingsService service, CancellationToken ct) =>
            service.CreateSiteAsync(r.Code ?? string.Empty, new SiteInput(r.Name, r.AllowedOrigins), ct));
        settings.MapPut("/sites/{id:guid}", (Guid id, SiteInput input, SiteSettingsService service, CancellationToken ct) =>
            service.UpdateSiteAsync(id, input, ct));
        settings.MapPost("/sites/{id:guid}/keys", (Guid id, SiteSettingsService service, CancellationToken ct) =>
            service.CreateKeyAsync(id, ct));
        settings.MapPost("/sites/{id:guid}/keys/{keyId:guid}/revoke",
            (Guid id, Guid keyId, SiteSettingsService service, CancellationToken ct) => service.RevokeKeyAsync(id, keyId, ct));

        // Виджеты
        settings.MapGet("/widgets", async (SiteSettingsService service, InternalSettings config, WidgetPublisher publisher,
            CancellationToken ct) => (await service.ListWidgetsAsync(ct)).Select(w => WithEmbed(w, config, publisher)));
        settings.MapPost("/widgets", async (CreateWidgetRequest r, SiteSettingsService service, InternalSettings config,
            WidgetPublisher publisher, CancellationToken ct) => WithEmbed(await service.CreateWidgetAsync(r.SiteId,
            new WidgetInput(r.Name, r.IdentificationMode, r.Title, r.Greeting, r.AccentColor, r.Position, r.DefaultLocale,
                r.TelegramBotId, r.Options), ct), config, publisher));
        settings.MapPut("/widgets/{id:guid}", async (Guid id, WidgetInput input, SiteSettingsService service,
            InternalSettings config, WidgetPublisher publisher, CancellationToken ct) =>
            WithEmbed(await service.UpdateWidgetAsync(id, input, ct), config, publisher));
        settings.MapDelete("/widgets/{id:guid}", async (Guid id, SiteSettingsService service, CancellationToken ct) =>
        {
            await service.DeleteWidgetAsync(id, ct);
            return Results.NoContent();
        });
        // Своя картинка кнопки чата: multipart, поле file.
        settings.MapPost("/widgets/{id:guid}/button-icon", async (Guid id, HttpRequest request, SiteSettingsService service,
            InternalSettings config, WidgetPublisher publisher, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                throw new DomainException(ErrorCodes.InvalidInput, "Send the image as multipart/form-data");
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new DomainException(ErrorCodes.InvalidInput, "Choose an image");
            var upload = (await FormFiles.ReadAsync(new FormFileCollection { file }, SiteSettingsService.ButtonIconMaxBytes, ct))[0];
            return WithEmbed(await service.SetButtonIconAsync(id, upload, ct), config, publisher);
        });

        // Публикация виджета в S3: новая версия, история версий, откат
        settings.MapPost("/widgets/{id:guid}/publish", async (Guid id, ClaimsPrincipal user, WidgetPublisher publisher,
            SiteSettingsService service, InternalSettings config, CancellationToken ct) =>
        {
            await publisher.PublishAsync(id, UserId(user), ct);
            return WithEmbed((await service.ListWidgetsAsync(ct)).Single(w => w.Id == id), config, publisher);
        });
        settings.MapGet("/widgets/{id:guid}/versions", (Guid id, WidgetPublisher publisher, CancellationToken ct) =>
            publisher.VersionsAsync(id, ct));
        settings.MapPost("/widgets/{id:guid}/versions/{version:int}/rollback", async (Guid id, int version,
            ClaimsPrincipal user, WidgetPublisher publisher, SiteSettingsService service, InternalSettings config,
            CancellationToken ct) =>
        {
            await publisher.RollbackAsync(id, version, UserId(user), ct);
            return WithEmbed((await service.ListWidgetsAsync(ct)).Single(w => w.Id == id), config, publisher);
        });

        settings.MapGet("/channels/counts", (SiteSettingsService service, CancellationToken ct) =>
            service.CountOpenTicketsAsync(ct));

        // Active Directory (LDAP)
        settings.MapGet("/ldap", (DirectoryService service, CancellationToken ct) => service.GetAsync(ct));
        settings.MapPut("/ldap", (LdapSettingsInput input, DirectoryService service, CancellationToken ct) =>
            service.SaveAsync(input, ct));
        settings.MapPost("/ldap/check", (DirectoryService service, CancellationToken ct) => service.CheckAsync(ct));
        settings.MapPost("/ldap/sync", (DirectoryService service, CancellationToken ct) => service.SyncAsync(ct));

        // Telegram-боты
        settings.MapGet("/telegram-bots", (TelegramBotSettingsService service, CancellationToken ct) =>
            service.ListAsync(ct));
        settings.MapPost("/telegram-bots", (TelegramBotInput input, TelegramBotSettingsService service,
            CancellationToken ct) => service.CreateAsync(input, ct));
        settings.MapPut("/telegram-bots/{id:guid}", (Guid id, TelegramBotInput input, TelegramBotSettingsService service,
            CancellationToken ct) => service.UpdateAsync(id, input, ct));
        settings.MapDelete("/telegram-bots/{id:guid}", async (Guid id, TelegramBotSettingsService service,
            CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // Пользователи
        settings.MapGet("/users", (UserAdminService service, CancellationToken ct) => service.ListAsync(ct));
        settings.MapPost("/users", (NewUserInput input, UserAdminService service, CancellationToken ct) =>
            service.CreateLocalAsync(input, ct));
        settings.MapPut("/users/{id:guid}", (Guid id, UserUpdateInput input, ClaimsPrincipal user,
            UserAdminService service, CancellationToken ct) => service.UpdateAsync(id, UserId(user), input, ct));
        settings.MapPut("/users/{id:guid}/status", (Guid id, SetStatusRequest r, UserAdminService service, CancellationToken ct) =>
            service.SetStatusAsync(id, r.Status, ct));
        settings.MapDelete("/users/{id:guid}", async (Guid id, ClaimsPrincipal user, UserAdminService service,
            CancellationToken ct) =>
        {
            await service.DeleteAsync(id, UserId(user), ct);
            return Results.NoContent();
        });

        // Группы агентов (отделы)
        settings.MapGet("/groups", (UserAdminService service, CancellationToken ct) => service.ListGroupsAsync(ct));
        settings.MapPost("/groups", (GroupRequest r, UserAdminService service, CancellationToken ct) =>
            service.CreateGroupAsync(r.Name, ct));
        settings.MapPut("/groups/{id:guid}", (Guid id, GroupRequest r, UserAdminService service, CancellationToken ct) =>
            service.RenameGroupAsync(id, r.Name, ct));
        settings.MapDelete("/groups/{id:guid}", async (Guid id, UserAdminService service, CancellationToken ct) =>
        {
            await service.DeleteGroupAsync(id, ct);
            return Results.NoContent();
        });

        settings.MapPost("/users/{id:guid}/password", async (Guid id, SetPasswordRequest r, UserAdminService service,
            CancellationToken ct) =>
        {
            await service.SetPasswordAsync(id, r.Password, ct);
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>
    /// Код вставки — одна строка на опубликованный файл widget_&lt;ключ&gt;.js в S3 (п. 3.5 ТЗ).
    /// Без S3 — прямое подключение ядра с публичного сервиса: виджет тогда берёт настройки по API.
    /// </summary>
    private static WidgetSettingsDto WithEmbed(WidgetView widget, InternalSettings config, WidgetPublisher publisher)
    {
        var fileUrl = publisher.FileUrl(widget.PublicKey);
        var embed = fileUrl is not null
            ? $"""<script async src="{fileUrl}"></script>"""
            : $"""<script async src="{config.PublicBaseUrl.TrimEnd('/')}/widget/core.js" data-relayo-widget="{widget.Id}"></script>""";
        return new(widget, embed, fileUrl, widget.Options.ButtonIcon is { } icon ? publisher.WidgetFileUrl(icon) : null);
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(TokenService.SubjectClaim)!);
}
