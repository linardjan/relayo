using System.Text.RegularExpressions;

namespace Relayo.Core.Sites;

public enum WidgetVisibility
{
    Always,
    Never,
}

public enum WidgetTooltipMode
{
    Never,
    Always,
    Hover,
}

public enum WidgetSound
{
    None,
    Beep,
}

public enum LauncherItemType
{
    Chat,
    Telegram,
    Link,
}

/// <summary>Пункт виджета: круглая кнопка рядом с кнопкой чата («Настройка пунктов» в Usedesk).</summary>
/// <param name="Url">Только для ссылки: куда она ведёт.</param>
public sealed record LauncherItem(LauncherItemType Type, string Label, string? Url = null);

/// <summary>
/// Внешний вид и поведение виджета на сайте (страница канала в админке). Хранится одним документом:
/// читается и меняется только вместе с виджетом, а на сайты попадает в опубликованном файле.
/// </summary>
public sealed partial record WidgetOptions
{
    public const int CaptionMaxLength = 300;
    public const int TooltipTextMaxLength = 200;
    public const int OperatorNameMaxLength = 64;
    public const int LabelMaxLength = 40;
    public const int UrlMaxLength = 500;
    public const int MaxItems = 6;

    public static readonly WidgetOptions Default = new();

    /// <summary>Текст подписи под полем ввода, например о политике обработки данных.</summary>
    public string Caption { get; init; } = string.Empty;

    public bool AllowAttachments { get; init; } = true;

    /// <summary>z-index виджета на странице сайта.</summary>
    public int ZIndex { get; init; } = 2147483000;

    /// <summary>Закрывать окно чата при клике вне его.</summary>
    public bool HideOnOutsideClick { get; init; }

    public WidgetVisibility Visibility { get; init; } = WidgetVisibility.Always;

    /// <summary>Через сколько секунд после открытия страницы появляется кнопка.</summary>
    public int ButtonDelaySeconds { get; init; }

    /// <summary>Цвет кнопок; пусто — цвет виджета.</summary>
    public string? ButtonColor { get; init; }

    /// <summary>Своя картинка кнопки чата: имя файла в бакете виджетов. Задаётся загрузкой.</summary>
    public string? ButtonIcon { get; init; }

    public WidgetTooltipMode TooltipMode { get; init; } = WidgetTooltipMode.Never;

    public string TooltipText { get; init; } = string.Empty;

    public string TooltipBackground { get; init; } = "#8fa0ab";

    public string TooltipColor { get; init; } = "#111111";

    public int TooltipTextSize { get; init; } = 14;

    /// <summary>Звук при новом сообщении оператора.</summary>
    public WidgetSound Sound { get; init; } = WidgetSound.None;

    /// <summary>Показывать посетителю это имя вместо псевдонима оператора.</summary>
    public string? OperatorName { get; init; }

    public IReadOnlyList<LauncherItem> Items { get; init; } = [new(LauncherItemType.Chat, "Online chat")];

    /// <summary>
    /// Проверяет и нормализует настройки. Пункт Telegram есть, только когда к виджету подключён бот:
    /// добавляется сам при подключении и убирается при отключении.
    /// </summary>
    public WidgetOptions Normalize(bool hasTelegramBot)
    {
        var caption = (Caption ?? string.Empty).Trim();
        var tooltip = (TooltipText ?? string.Empty).Trim();
        var operatorName = OperatorName?.Trim() is { Length: > 0 } name ? name : null;
        if (caption.Length > CaptionMaxLength)
            throw Invalid($"Caption must be up to {CaptionMaxLength} characters");
        if (tooltip.Length > TooltipTextMaxLength)
            throw Invalid($"Tooltip text must be up to {TooltipTextMaxLength} characters");
        if (operatorName is { Length: > OperatorNameMaxLength })
            throw Invalid($"Operator name must be up to {OperatorNameMaxLength} characters");
        if (ZIndex is < 0 or > int.MaxValue - 1)
            throw Invalid("z-index must be zero or greater");
        if (ButtonDelaySeconds is < 0 or > 3600)
            throw Invalid("Button delay must be 0-3600 seconds");
        if (TooltipTextSize is < 10 or > 32)
            throw Invalid("Tooltip text size must be 10-32");
        if (!Enum.IsDefined(Visibility) || !Enum.IsDefined(TooltipMode) || !Enum.IsDefined(Sound))
            throw Invalid("Unknown widget option value");
        var buttonColor = string.IsNullOrWhiteSpace(ButtonColor) ? null : Color(ButtonColor, "Button color");

        var items = new List<LauncherItem>();
        foreach (var item in Items ?? [])
        {
            if (!Enum.IsDefined(item.Type))
                throw Invalid("Unknown item type");
            var label = (item.Label ?? string.Empty).Trim();
            if (label.Length > LabelMaxLength)
                throw Invalid($"Item label must be up to {LabelMaxLength} characters");
            string? url = null;
            if (item.Type == LauncherItemType.Link)
            {
                url = item.Url?.Trim();
                if (url is null || url.Length > UrlMaxLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw Invalid("A link item needs an address like https://example.com");
            }
            else if (items.Any(i => i.Type == item.Type))
            {
                throw Invalid($"Only one {item.Type} item is allowed");
            }
            // Бот не выбран или отключён от виджета: пункт Telegram пропадает.
            if (item.Type == LauncherItemType.Telegram && !hasTelegramBot)
                continue;
            items.Add(new LauncherItem(item.Type, label.Length > 0 ? label : DefaultLabel(item.Type), url));
        }
        if (!items.Any(i => i.Type == LauncherItemType.Chat))
            items.Insert(0, new LauncherItem(LauncherItemType.Chat, DefaultLabel(LauncherItemType.Chat)));
        if (hasTelegramBot && !items.Any(i => i.Type == LauncherItemType.Telegram))
            items.Add(new LauncherItem(LauncherItemType.Telegram, DefaultLabel(LauncherItemType.Telegram)));
        if (items.Count > MaxItems)
            throw Invalid($"Up to {MaxItems} items");

        return this with
        {
            Caption = caption,
            TooltipText = tooltip,
            OperatorName = operatorName,
            ButtonColor = buttonColor,
            TooltipBackground = Color(TooltipBackground, "Tooltip background"),
            TooltipColor = Color(TooltipColor, "Tooltip text color"),
            Items = items,
        };
    }

    public static string DefaultLabel(LauncherItemType type) => type switch
    {
        LauncherItemType.Chat => "Online chat",
        LauncherItemType.Telegram => "Telegram",
        _ => "Link",
    };

    private static string Color(string? value, string what)
    {
        value = value?.Trim() ?? string.Empty;
        return ColorPattern().IsMatch(value) ? value.ToLowerInvariant() : throw Invalid($"{what} must look like #1677ff");
    }

    private static DomainException Invalid(string message) => new(ErrorCodes.InvalidInput, message);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();
}
