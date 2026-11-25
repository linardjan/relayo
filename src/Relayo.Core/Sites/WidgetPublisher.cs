using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relayo.Core.Data;

namespace Relayo.Core.Sites;

/// <summary>Хранилище файлов (S3 / MinIO). Реализация — во внутреннем сервисе.</summary>
public interface IFileStorage
{
    bool IsConfigured { get; }

    /// <summary>Кладёт файл в публичный бакет виджетов.</summary>
    Task PutWidgetFileAsync(string name, byte[] content, string cacheControl, CancellationToken ct);
}

/// <param name="PublicBaseUrl">Адрес публичного сервиса: API виджета и ядро core.js.</param>
/// <param name="FilesBaseUrl">Адрес, по которому браузеры читают файлы виджетов из бакета.</param>
public sealed record WidgetPublishingOptions(string PublicBaseUrl, string? FilesBaseUrl);

/// <summary>
/// Настройки виджета на момент публикации. Совпадают с ответом /api/widget/{id}/config публичного сервиса,
/// поэтому ядро виджета может взять их прямо из файла и не запрашивать при каждом открытии страницы.
/// </summary>
public sealed record WidgetSnapshot(
    string WidgetId,
    string Title,
    string Greeting,
    string AccentColor,
    string Position,
    string IdentificationMode,
    string DefaultLocale,
    int HistoryDays,
    string? TelegramBot);

/// <summary>Опубликованная версия виджета. Откат — повторная публикация её настроек.</summary>
public sealed class WidgetVersion
{
    private WidgetVersion()
    {
    }

    public WidgetVersion(Guid widgetId, int version, string snapshotJson, string publishedBy, string? note, DateTimeOffset now)
    {
        WidgetId = widgetId;
        Version = version;
        SnapshotJson = snapshotJson;
        PublishedBy = publishedBy;
        Note = note;
        PublishedAt = now;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid WidgetId { get; private set; }

    public int Version { get; private set; }

    /// <summary>Настройки виджета (<see cref="WidgetSnapshot"/>) в JSON, плюс название и id бота для отката.</summary>
    public string SnapshotJson { get; private set; } = string.Empty;

    public string PublishedBy { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }
}

public sealed record WidgetVersionView(int Version, DateTimeOffset PublishedAt, string PublishedBy, string? Note);

/// <summary>
/// Публикация виджетов в S3 (п. 3.5 ТЗ). Файл widget_&lt;ключ&gt;.js — всегда последняя версия (кэш 5 минут),
/// widget_&lt;ключ&gt;.v&lt;N&gt;.js — конкретная версия (кэш навсегда). Файл содержит настройки и маленький загрузчик,
/// который подключает общее ядро core.js с публичного сервиса.
/// </summary>
public sealed class WidgetPublisher(
    RelayoDbContext db,
    IFileStorage storage,
    WidgetPublishingOptions options,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Для файла виджета: кавычки и кириллица без \u-экранирования, чтобы файл читался глазами.
    // Это отдельный .js-файл, а не HTML, поэтому мягкое экранирование безопасно.
    private static readonly JsonSerializerOptions ScriptJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Адрес файла виджета для кода вставки или null, если хранилище не настроено.</summary>
    public string? FileUrl(string publicKey) =>
        options.FilesBaseUrl is { Length: > 0 } baseUrl ? $"{baseUrl.TrimEnd('/')}/{FileName(publicKey)}" : null;

    public static string FileName(string publicKey) => $"widget_{publicKey}.js";

    public static string VersionFileName(string publicKey, int version) => $"widget_{publicKey}.v{version}.js";

    public async Task<IReadOnlyList<WidgetVersionView>> VersionsAsync(Guid widgetId, CancellationToken ct) =>
        await db.WidgetVersions.AsNoTracking().Where(v => v.WidgetId == widgetId)
            .OrderByDescending(v => v.Version).Take(100)
            .Select(v => new WidgetVersionView(v.Version, v.PublishedAt, v.PublishedBy, v.Note))
            .ToListAsync(ct);

    public async Task PublishAsync(Guid widgetId, Guid? userId, CancellationToken ct) =>
        await PublishAsync(await FindAsync(widgetId, ct), userId, note: null, ct);

    /// <summary>Откат: настройки виджета возвращаются к версии и публикуются заново как новая версия.</summary>
    public async Task RollbackAsync(Guid widgetId, int version, Guid userId, CancellationToken ct)
    {
        var widget = await FindAsync(widgetId, ct);
        var old = await db.WidgetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.WidgetId == widgetId && v.Version == version, ct)
                  ?? throw new DomainException(ErrorCodes.InvalidInput, $"Version {version} not found");
        var stored = JsonSerializer.Deserialize<StoredSnapshot>(old.SnapshotJson, Json)!;
        var s = stored.Config;
        var botId = stored.TelegramBotId is { } id && await db.TelegramBots.AnyAsync(b => b.Id == id, ct) ? id : (Guid?)null;
        widget.Configure(stored.Name, s.IdentificationMode == "direct" ? IdentificationMode.Direct : IdentificationMode.SignedToken,
            s.Title, s.Greeting, s.AccentColor, s.Position == "left" ? WidgetPosition.Left : WidgetPosition.Right,
            s.DefaultLocale, botId, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await PublishAsync(widget, userId, $"Rollback to v{version}", ct);
    }

    private async Task PublishAsync(Widget widget, Guid? userId, string? note, CancellationToken ct)
    {
        if (!storage.IsConfigured || options.FilesBaseUrl is not { Length: > 0 })
            throw new DomainException(ErrorCodes.StorageNotConfigured,
                "File storage (S3) is not configured: set S3_ENDPOINT, S3_ACCESS_KEY, S3_SECRET_KEY and WIDGET_FILES_BASE_URL");

        var bot = widget.TelegramBotId is { } botId
            ? await db.TelegramBots.Where(b => b.Id == botId && b.IsEnabled).Select(b => b.Username).SingleOrDefaultAsync(ct)
            : null;
        var snapshot = new WidgetSnapshot(widget.Id.ToString(), widget.Title, widget.Greeting, widget.AccentColor,
            widget.Position == WidgetPosition.Left ? "left" : "right",
            widget.IdentificationMode == IdentificationMode.SignedToken ? "signed_token" : "direct",
            widget.DefaultLocale, Conversations.WidgetConversationService.HistoryDays, bot);

        var now = time.GetUtcNow();
        var version = (await db.WidgetVersions.Where(v => v.WidgetId == widget.Id).MaxAsync(v => (int?)v.Version, ct) ?? 0) + 1;
        var script = Encoding.UTF8.GetBytes(BuildScript(widget.PublicKey, version, snapshot, now));

        // Сначала версия (кэш навсегда), потом «последняя» (кэш 5 минут): сайты не увидят ссылку на несуществующий файл.
        await storage.PutWidgetFileAsync(VersionFileName(widget.PublicKey, version), script, "public, max-age=31536000, immutable", ct);
        await storage.PutWidgetFileAsync(FileName(widget.PublicKey), script, "public, max-age=300", ct);

        var publishedBy = userId is null
            ? "System"
            : await db.Users.Where(u => u.Id == userId).Select(u => u.FullName).SingleOrDefaultAsync(ct) ?? "—";
        var stored = new StoredSnapshot(widget.Name, widget.TelegramBotId, snapshot);
        db.WidgetVersions.Add(new WidgetVersion(widget.Id, version, JsonSerializer.Serialize(stored, Json), publishedBy, note, now));
        widget.MarkPublished(version, now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Загрузчик: добавляет на страницу ядро core.js и передаёт ему id виджета, адрес API и настройки.
    /// Повторное подключение на той же странице ничего не делает.
    /// </summary>
    internal string BuildScript(string publicKey, int version, WidgetSnapshot snapshot, DateTimeOffset now)
    {
        var baseUrl = options.PublicBaseUrl.TrimEnd('/');
        string Js(string value) => JsonSerializer.Serialize(value, ScriptJson);
        return $$"""
            /* Relayo widget {{publicKey}} v{{version}}, published {{now:yyyy-MM-dd HH:mm}} UTC */
            (function (w, d) {
              if (w.__relayoWidgetLoaded) return;
              w.__relayoWidgetLoaded = true;
              var s = d.createElement('script');
              s.async = true;
              s.src = {{Js(baseUrl + "/widget/core.js")}};
              s.setAttribute('data-relayo-widget', {{Js(snapshot.WidgetId)}});
              s.setAttribute('data-relayo-api', {{Js(baseUrl)}});
              s.setAttribute('data-relayo-config', {{Js(JsonSerializer.Serialize(snapshot, ScriptJson))}});
              (d.head || d.body || d.documentElement).appendChild(s);
            })(window, document);

            """;
    }

    private async Task<Widget> FindAsync(Guid widgetId, CancellationToken ct) =>
        await db.Widgets.SingleOrDefaultAsync(w => w.Id == widgetId, ct)
        ?? throw new DomainException(ErrorCodes.WidgetNotFound, "Widget not found");

    private sealed record StoredSnapshot(string Name, Guid? TelegramBotId, WidgetSnapshot Config);
}
