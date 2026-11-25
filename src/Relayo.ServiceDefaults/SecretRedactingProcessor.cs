using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace Relayo.ServiceDefaults;

/// <summary>
/// Убирает секреты из адресов в трассировке перед отправкой в Seq.
/// Telegram Bot API требует токен бота в пути запроса: https://api.telegram.org/bot&lt;token&gt;/sendMessage.
/// </summary>
internal sealed partial class SecretRedactingProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlTags = ["url.full", "url.path", "http.url", "http.target"];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is string value && BotToken().IsMatch(value))
                activity.SetTag(tag, Redact(value));
        }
        if (BotToken().IsMatch(activity.DisplayName))
            activity.DisplayName = Redact(activity.DisplayName);
    }

    internal static string Redact(string value) => BotToken().Replace(value, "/bot***/");

    [GeneratedRegex(@"/bot\d+:[^/?#]+/")]
    private static partial Regex BotToken();
}
