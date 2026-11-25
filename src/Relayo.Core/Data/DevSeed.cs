using Microsoft.EntityFrameworkCore;
using Relayo.Core.Security;
using Relayo.Core.Sites;

namespace Relayo.Core.Data;

/// <summary>Параметры тестового сайта из .env (только Development, п. 2.3 ТЗ).</summary>
public sealed record TestSiteSeed(
    string Product,
    IReadOnlyList<string> Origins,
    string KeyId,
    string Secret,
    Guid TokenModeWidgetId,
    Guid DirectModeWidgetId,
    string TokenModeWidgetKey,
    string DirectModeWidgetKey);

/// <summary>
/// Создаёт или обновляет тестовый сайт, его ключ и два виджета, чтобы TestSite
/// работал сразу после запуска без настройки в админке.
/// </summary>
internal static class DevSeed
{
    public static async Task ApplyAsync(RelayoDbContext db, SecretProtector protector, TestSiteSeed seed,
        DateTimeOffset now, CancellationToken ct)
    {
        var site = await db.Sites.SingleOrDefaultAsync(s => s.Code == seed.Product, ct);
        if (site is null)
        {
            site = new Site(Guid.CreateVersion7(), seed.Product, "Test site", seed.Origins, now);
            db.Sites.Add(site);
        }
        else
        {
            site.Update(site.Name, seed.Origins);
        }

        var key = await db.SiteKeys.SingleOrDefaultAsync(k => k.KeyId == seed.KeyId, ct);
        if (key is null)
            db.SiteKeys.Add(new SiteKey(site.Id, seed.KeyId, protector.Protect(seed.Secret), now));
        else if (protector.Unprotect(key.ProtectedSecret) != seed.Secret)
            key.ReplaceSecret(protector.Protect(seed.Secret));

        await UpsertWidgetAsync(db, seed.TokenModeWidgetId, seed.TokenModeWidgetKey, site.Id, "Test widget (signed token)",
            IdentificationMode.SignedToken, now, ct);
        await UpsertWidgetAsync(db, seed.DirectModeWidgetId, seed.DirectModeWidgetKey, site.Id, "Test widget (direct clientId)",
            IdentificationMode.Direct, now, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Виджет создаётся один раз; дальше его настраивают в админке, и при запуске правки не затираются.
    /// Ключ берётся из .env: по нему TestSite знает имя файла виджета (widget_&lt;ключ&gt;.js).
    /// </summary>
    private static async Task UpsertWidgetAsync(RelayoDbContext db, Guid id, string key, Guid siteId, string name,
        IdentificationMode mode, DateTimeOffset now, CancellationToken ct)
    {
        var widget = await db.Widgets.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (widget is null)
        {
            widget = new Widget(id, siteId, name, mode, now);
            widget.Configure(name, mode, "Support", "Hi! How can we help you?", "#1677ff", WidgetPosition.Right, "en",
                null, WidgetOptions.Default, now);
            db.Widgets.Add(widget);
        }
        if (widget.PublicKey != key)
            widget.SetPublicKey(key);
    }
}
