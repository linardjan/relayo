namespace Relayo.Core.Automation;

/// <summary>Какое значение ждёт условие или действие. По нему админка выбирает поле ввода.</summary>
public enum RuleValueKind
{
    None,
    Text,
    ChannelType,
    Channel,
    Site,
    Status,
    User,
    Boolean,
}

public sealed record RuleTriggerType(string Type, bool HasMinutes);

public sealed record RuleConditionField(string Field, IReadOnlyList<string> Operators, RuleValueKind Value);

public sealed record RuleActionType(string Type, RuleValueKind Value);

/// <summary>
/// Справочник событий, условий и действий. Сервер принимает только то, что здесь есть;
/// подписи для людей живут в файлах локализации админки.
/// </summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleTriggerType> Triggers =
    [
        new("client_message", false),
        new("ticket_created", false),
        new("status_changed", false),
        new("operator_replied", false),
        new("ticket_assigned", false),
        new("ticket_closed", false),
        new("out_of_hours_message", false),
        new("returning_client", false),
        // Таймеры: «оператор не ответил за N минут» (п. 2.1 ТЗ, отложенные сообщения Wolverine).
        new("no_operator_reply", true),
        new("no_client_reply", true),
    ];

    private static readonly string[] Equality = ["eq", "neq"];

    public static readonly IReadOnlyList<RuleConditionField> Conditions =
    [
        new("channel_type", Equality, RuleValueKind.ChannelType),
        new("channel", Equality, RuleValueKind.Channel),
        new("site", Equality, RuleValueKind.Site),
        new("status", Equality, RuleValueKind.Status),
        new("assignee", Equality, RuleValueKind.User),
        new("client_verified", ["eq"], RuleValueKind.Boolean),
        new("message_text", ["contains", "not_contains", "eq", "regex"], RuleValueKind.Text),
        new("client_locale", Equality, RuleValueKind.Text),
    ];

    public static readonly IReadOnlyList<RuleActionType> Actions =
    [
        new("send_message", RuleValueKind.Text),
        new("set_status", RuleValueKind.Status),
        new("assign", RuleValueKind.User),
        new("unassign", RuleValueKind.None),
        new("close_ticket", RuleValueKind.None),
    ];
}
