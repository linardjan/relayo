using System.Net;
using System.Text.RegularExpressions;
using Relayo.Core.Conversations;

namespace Relayo.Core.Automation;

/// <summary>Всё, что известно о тикете в момент события: по этим данным проверяются условия правила.</summary>
public sealed record RuleContext(
    Channel? ChannelType,
    string? ChannelKey,
    Guid SiteId,
    TicketStatus Status,
    Guid? AssigneeId,
    bool ClientVerified,
    string? MessageText,
    string? ClientLocale);

/// <summary>Проверка условий правила. Без БД и побочных эффектов, поэтому легко тестируется.</summary>
public static partial class RuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// «Все условия» должны выполниться все; из «Одного из условий» — хотя бы одно, если группа не пустая.
    /// </summary>
    public static bool Matches(Rule rule, RuleContext context) =>
        rule.Definition.WhatAll.All(c => Matches(c, context, rule))
        && (rule.Definition.WhatAny.Count == 0 || rule.Definition.WhatAny.Any(c => Matches(c, context, rule)));

    public static bool Matches(RuleCondition condition, RuleContext ctx, Rule rule)
    {
        var value = condition.Value ?? string.Empty;
        bool Equal(string? actual) => string.Equals(actual, value, StringComparison.OrdinalIgnoreCase);
        bool Compare(bool equals) => condition.Operator == "neq" ? !equals : equals;

        return condition.Field switch
        {
            "channel_type" => Compare(Equal(ctx.ChannelType?.ToString())),
            "channel" => Compare(Equal(ctx.ChannelKey)),
            "site" => Compare(Equal(ctx.SiteId.ToString())),
            "status" => Compare(Equal(ctx.Status.ToString())),
            "assignee" => Compare(value == "none" ? ctx.AssigneeId is null : Equal(ctx.AssigneeId?.ToString())),
            "client_verified" => ctx.ClientVerified == (value == "true"),
            "client_locale" => Compare(ctx.ClientLocale is not null
                                       && (Equal(ctx.ClientLocale) || ctx.ClientLocale.StartsWith(value + "-", StringComparison.OrdinalIgnoreCase))),
            "message_text" => MatchesText(condition.Operator, value, ctx.MessageText, rule.UseWordForms, rule.IgnoreHtml),
            _ => false,
        };
    }

    public static bool MatchesText(string op, string pattern, string? text, bool useWordForms, bool ignoreHtml)
    {
        text ??= string.Empty;
        if (ignoreHtml)
            text = WebUtility.HtmlDecode(HtmlTag().Replace(text, " "));

        switch (op)
        {
            case "regex":
                try
                {
                    return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            case "eq":
                if (!useWordForms)
                    return string.Equals(Normalize(text), Normalize(pattern), StringComparison.Ordinal);
                var textWords = Words(text).ToList();
                var patternWords = Words(pattern).ToList();
                return textWords.Count == patternWords.Count && textWords.Zip(patternWords).All(p => SameWord(p.First, p.Second));
            case "contains":
                return Contains(text, pattern, useWordForms);
            case "not_contains":
                return !Contains(text, pattern, useWordForms);
            default:
                return false;
        }
    }

    private static bool Contains(string text, string pattern, bool useWordForms)
    {
        if (!useWordForms)
            return Normalize(text).Contains(Normalize(pattern), StringComparison.Ordinal);
        // Со словоформами: все слова условия встречаются в тексте в любой форме.
        var words = Words(text).ToList();
        var wanted = Words(pattern).ToList();
        return wanted.Count > 0 && wanted.All(w => words.Any(t => SameWord(t, w)));
    }

    /// <summary>Подставляет переменные в текст автоответа: {client.name}, {operator.alias}, {ticket.number}.</summary>
    public static string Render(string template, string? clientName, string? operatorAlias, long ticketNumber) =>
        template
            .Replace("{client.name}", clientName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{operator.alias}", operatorAlias ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{ticket.number}", ticketNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static string Normalize(string value) =>
        Whitespace().Replace(value.ToLowerInvariant().Replace('ё', 'е'), " ").Trim();

    /// <summary>
    /// Упрощённые основы слов: отбрасываются частые окончания русского и английского.
    /// «оплата», «оплаты», «оплатой» → «оплат». Это не полноценная морфология, но для условий правил хватает.
    /// </summary>
    internal static IEnumerable<string> Words(string text) => Word().Matches(Normalize(text)).Select(m => m.Value);

    /// <summary>
    /// Одно слово в разных формах. Основы равны, либо каждое слово начинается с основы другого:
    /// «депозит» (основа «депоз») и «депозиты» (основа «депозит») — одно слово; «карта» и «картошка» — нет.
    /// </summary>
    internal static bool SameWord(string a, string b)
    {
        var (sa, sb) = (Stem(a), Stem(b));
        return sa == sb || (a.StartsWith(sb, StringComparison.Ordinal) && b.StartsWith(sa, StringComparison.Ordinal));
    }

    private static readonly string[] RussianEndings =
    [
        "иями", "ями", "ами", "ией", "иям", "ием", "иях", "ого", "его", "ому", "ему", "ыми", "ими", "ться", "тся",
        "ешь", "ая", "яя", "ое", "ее", "ые", "ие", "ый", "ий", "ой", "ей", "ам", "ям", "ах", "ях", "ом", "ем", "ов",
        "ев", "ия", "ью", "ья", "ье", "ет", "ют", "ут", "ит", "ат", "ят", "ть", "ла", "ло", "ли",
        "и", "ы", "а", "я", "о", "е", "у", "ю", "ь", "й", "л",
    ];

    private static readonly string[] EnglishEndings = ["ings", "ing", "ies", "ied", "ed", "es", "s"];

    private static string Stem(string word)
    {
        var endings = word.Any(c => c is >= 'а' and <= 'я') ? RussianEndings : EnglishEndings;
        foreach (var ending in endings)
        {
            // Основа не короче трёх букв: иначе «дом» и «да» стали бы одним словом.
            if (word.Length - ending.Length >= 3 && word.EndsWith(ending, StringComparison.Ordinal))
                return word[..^ending.Length];
        }
        return word;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
