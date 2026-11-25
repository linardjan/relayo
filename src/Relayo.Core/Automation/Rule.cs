namespace Relayo.Core.Automation;

/// <summary>Событие, на которое срабатывает правило (блок «When»).</summary>
public sealed record RuleTrigger(string Type, int? Minutes = null);

/// <summary>Условие на тикет, клиента или сообщение (блок «What»).</summary>
public sealed record RuleCondition(string Field, string Operator, string? Value);

/// <summary>Действие (блок «Do»). Значение зависит от типа: текст, статус, id оператора.</summary>
public sealed record RuleAction(string Type, string? Value = null);

/// <summary>
/// Что делает правило. «Все условия» должны выполниться все, из «Одного из условий» — хотя бы одно
/// (если группа не пустая).
/// </summary>
public sealed record RuleDefinition(
    IReadOnlyList<RuleTrigger> WhenAll,
    IReadOnlyList<RuleTrigger> WhenAny,
    IReadOnlyList<RuleCondition> WhatAll,
    IReadOnlyList<RuleCondition> WhatAny,
    IReadOnlyList<RuleAction> Actions)
{
    public static readonly RuleDefinition Empty = new([], [], [], [], []);
}

/// <summary>
/// Автоматическое правило: событие → условия → действия (п. 3.6 ТЗ, этап 5). Порядок в списке — порядок применения.
/// Само выполнение правил появится на этапе 5; сейчас правила настраиваются и хранятся.
/// </summary>
public sealed class Rule
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 4000;

    private Rule()
    {
    }

    public Rule(string name, int position, DateTimeOffset now)
    {
        Name = name;
        Position = position;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    /// <summary>Правило запускается только вручную, из раздела «Run rules manually».</summary>
    public bool IsManual { get; private set; }

    /// <summary>Условия на текст учитывают словоформы: «оплата» найдёт и «оплатой».</summary>
    public bool UseWordForms { get; private set; }

    /// <summary>Условия на текст не учитывают HTML-разметку сообщения.</summary>
    public bool IgnoreHtml { get; private set; } = true;

    /// <summary>Сработав, правило останавливает обработку следующих по порядку правил (п. 3.6 ТЗ).</summary>
    public bool StopProcessing { get; private set; }

    public int Position { get; private set; }

    public RuleDefinition Definition { get; private set; } = RuleDefinition.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string name, string? description, bool enabled, bool manual, bool useWordForms, bool ignoreHtml,
        bool stopProcessing, RuleDefinition definition, DateTimeOffset now)
    {
        StopProcessing = stopProcessing;
        Name = name;
        Description = description;
        IsEnabled = enabled;
        IsManual = manual;
        UseWordForms = useWordForms;
        IgnoreHtml = ignoreHtml;
        Definition = definition;
        UpdatedAt = now;
    }

    public void MoveTo(int position) => Position = position;
}

/// <summary>Запись истории изменений правила: кто, когда и что поменял.</summary>
public sealed class RuleRevision
{
    public const int SummaryMaxLength = 1000;

    private RuleRevision()
    {
    }

    public RuleRevision(Guid ruleId, Guid? userId, string userName, string summary, DateTimeOffset now)
    {
        RuleId = ruleId;
        UserId = userId;
        UserName = userName;
        Summary = summary.Length <= SummaryMaxLength ? summary : summary[..SummaryMaxLength];
        CreatedAt = now;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid RuleId { get; private set; }

    public Guid? UserId { get; private set; }

    /// <summary>ФИО на момент изменения: история читается, даже если сотрудника потом переименуют.</summary>
    public string UserName { get; private set; } = string.Empty;

    public string Summary { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Срабатывание правила (п. 3.6 ТЗ: каждое срабатывание пишется в лог — какое правило и почему).</summary>
public sealed class RuleExecution
{
    public const int DetailsMaxLength = 2000;

    private RuleExecution()
    {
    }

    public RuleExecution(Guid ruleId, string ruleName, Guid ticketId, long ticketNumber, string trigger, bool applied,
        string details, DateTimeOffset now)
    {
        RuleId = ruleId;
        RuleName = ruleName;
        TicketId = ticketId;
        TicketNumber = ticketNumber;
        Trigger = trigger;
        Applied = applied;
        Details = details.Length <= DetailsMaxLength ? details : details[..DetailsMaxLength];
        CreatedAt = now;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid RuleId { get; private set; }

    /// <summary>Название на момент срабатывания: журнал читается, даже если правило потом переименуют или удалят.</summary>
    public string RuleName { get; private set; } = string.Empty;

    public Guid TicketId { get; private set; }

    public long TicketNumber { get; private set; }

    /// <summary>Событие, на которое сработало правило.</summary>
    public string Trigger { get; private set; } = string.Empty;

    /// <summary>Действия выполнены. false — условия подошли, но действия пропущены (например, лимит автоответов).</summary>
    public bool Applied { get; private set; }

    public string Details { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
}
