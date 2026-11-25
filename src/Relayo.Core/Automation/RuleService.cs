using Microsoft.EntityFrameworkCore;
using Relayo.Core.Conversations;
using Relayo.Core.Data;

namespace Relayo.Core.Automation;

public sealed record RuleSummary(Guid Id, string Name, bool IsEnabled, bool IsManual, int Position, DateTimeOffset UpdatedAt);

public sealed record RuleExecutionView(DateTimeOffset CreatedAt, string RuleName, long TicketNumber, string Trigger,
    bool Applied, string Details);

public sealed record RuleRevisionView(Guid Id, string UserName, string Summary, DateTimeOffset CreatedAt);

public sealed record RuleView(
    Guid Id,
    string Name,
    string? Description,
    bool IsEnabled,
    bool IsManual,
    bool UseWordForms,
    bool IgnoreHtml,
    bool StopProcessing,
    RuleDefinition Definition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RuleRevisionView> Revisions);

public sealed record RuleInput(
    string Name,
    string? Description,
    bool IsEnabled,
    bool IsManual,
    bool UseWordForms,
    bool IgnoreHtml,
    bool StopProcessing,
    RuleDefinition Definition);

/// <summary>Автоматические правила: список с порядком применения, настройка, история изменений.</summary>
public sealed class RuleService(RelayoDbContext db, TimeProvider time)
{
    private const int MaxItemsPerGroup = 50;
    private const int MaxMinutes = 7 * 24 * 60;
    private const int RevisionsShown = 100;

    public async Task<IReadOnlyList<RuleSummary>> ListAsync(CancellationToken ct) =>
        await db.Rules.AsNoTracking().OrderBy(r => r.Position).ThenBy(r => r.CreatedAt)
            .Select(r => new RuleSummary(r.Id, r.Name, r.IsEnabled, r.IsManual, r.Position, r.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>Журнал срабатываний за последние дни: для выгрузки в CSV.</summary>
    public async Task<IReadOnlyList<RuleExecutionView>> ExecutionsAsync(int days, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90));
        return await db.RuleExecutions.AsNoTracking().Where(e => e.CreatedAt >= since)
            .OrderByDescending(e => e.CreatedAt).Take(50_000)
            .Select(e => new RuleExecutionView(e.CreatedAt, e.RuleName, e.TicketNumber, e.Trigger, e.Applied, e.Details))
            .ToListAsync(ct);
    }

    public async Task<RuleView> GetAsync(Guid id, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        var revisions = await db.RuleRevisions.AsNoTracking().Where(r => r.RuleId == id)
            .OrderByDescending(r => r.CreatedAt).Take(RevisionsShown)
            .Select(r => new RuleRevisionView(r.Id, r.UserName, r.Summary, r.CreatedAt))
            .ToListAsync(ct);
        return ToView(rule, revisions);
    }

    public async Task<RuleView> CreateAsync(RuleInput input, Guid userId, CancellationToken ct)
    {
        var (name, description, definition) = await ValidateAsync(input, ct);
        var now = time.GetUtcNow();
        // Новое правило встаёт в конец списка: последним по порядку применения.
        var position = (await db.Rules.MaxAsync(r => (int?)r.Position, ct) ?? -1) + 1;
        var rule = new Rule(name, position, now);
        rule.Update(name, description, input.IsEnabled, input.IsManual, input.UseWordForms, input.IgnoreHtml, input.StopProcessing, definition, now);
        db.Rules.Add(rule);
        db.RuleRevisions.Add(new RuleRevision(rule.Id, userId, await UserNameAsync(userId, ct), "Created", now));
        await db.SaveChangesAsync(ct);
        return await GetAsync(rule.Id, ct);
    }

    public async Task<RuleView> UpdateAsync(Guid id, RuleInput input, Guid userId, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        var (name, description, definition) = await ValidateAsync(input, ct);
        var changes = Describe(rule, name, description, input, definition);
        if (changes.Count == 0)
            return await GetAsync(id, ct);

        var now = time.GetUtcNow();
        rule.Update(name, description, input.IsEnabled, input.IsManual, input.UseWordForms, input.IgnoreHtml, input.StopProcessing, definition, now);
        db.RuleRevisions.Add(new RuleRevision(rule.Id, userId, await UserNameAsync(userId, ct), string.Join("; ", changes), now));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await FindAsync(id, ct);
        db.Rules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Новый порядок применения: id правил сверху вниз. Правила, которых нет в списке, идут после.</summary>
    public async Task<IReadOnlyList<RuleSummary>> ReorderAsync(IReadOnlyList<Guid> order, CancellationToken ct)
    {
        var rules = await db.Rules.OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ToListAsync(ct);
        var ordered = order.Distinct().Select(id => rules.FirstOrDefault(r => r.Id == id)).OfType<Rule>()
            .Concat(rules.Where(r => !order.Contains(r.Id)))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].MoveTo(i);
        await db.SaveChangesAsync(ct);
        return await ListAsync(ct);
    }

    private async Task<(string Name, string? Description, RuleDefinition Definition)> ValidateAsync(RuleInput input,
        CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > Rule.NameMaxLength)
            throw Invalid($"Name must be 1-{Rule.NameMaxLength} characters");
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description is { Length: > Rule.DescriptionMaxLength })
            throw Invalid($"Description must be up to {Rule.DescriptionMaxLength} characters");

        var d = input.Definition ?? RuleDefinition.Empty;
        var whenAll = d.WhenAll ?? [];
        var whenAny = d.WhenAny ?? [];
        var whatAll = d.WhatAll ?? [];
        var whatAny = d.WhatAny ?? [];
        var actions = d.Actions ?? [];
        if (new[] { whenAll.Count, whenAny.Count, whatAll.Count, whatAny.Count, actions.Count }.Any(c => c > MaxItemsPerGroup))
            throw Invalid($"Up to {MaxItemsPerGroup} items per block");
        if (whenAll.Count + whenAny.Count == 0 && !input.IsManual)
            throw Invalid("Add at least one event in “When” or mark the rule as manual");
        if (actions.Count == 0)
            throw Invalid("Add at least one action in “Do”");

        foreach (var trigger in whenAll.Concat(whenAny))
        {
            var type = RuleCatalog.Triggers.FirstOrDefault(x => x.Type == trigger.Type)
                       ?? throw Invalid($"Unknown event “{trigger.Type}”");
            if (type.HasMinutes && trigger.Minutes is not (> 0 and <= MaxMinutes))
                throw Invalid($"Set the number of minutes (1-{MaxMinutes}) for a timer event");
        }

        foreach (var condition in whatAll.Concat(whatAny))
        {
            var field = RuleCatalog.Conditions.FirstOrDefault(x => x.Field == condition.Field)
                        ?? throw Invalid($"Unknown condition “{condition.Field}”");
            if (!field.Operators.Contains(condition.Operator))
                throw Invalid($"Operator “{condition.Operator}” does not fit “{condition.Field}”");
            await ValidateValueAsync(field.Value, condition.Value, ct);
            if (condition.Operator == "regex" && !IsValidRegex(condition.Value!))
                throw Invalid($"“{condition.Value}” is not a valid regular expression");
        }

        foreach (var action in actions)
        {
            var type = RuleCatalog.Actions.FirstOrDefault(x => x.Type == action.Type)
                       ?? throw Invalid($"Unknown action “{action.Type}”");
            await ValidateValueAsync(type.Value, action.Value, ct);
        }

        var normalize = (string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return (name, description, new RuleDefinition(
            whenAll.Select(x => x with { Minutes = Minutes(x) }).ToList(),
            whenAny.Select(x => x with { Minutes = Minutes(x) }).ToList(),
            whatAll.Select(x => x with { Value = normalize(x.Value) }).ToList(),
            whatAny.Select(x => x with { Value = normalize(x.Value) }).ToList(),
            actions.Select(x => x with { Value = normalize(x.Value) }).ToList()));
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int? Minutes(RuleTrigger trigger) =>
        RuleCatalog.Triggers.First(t => t.Type == trigger.Type).HasMinutes ? trigger.Minutes : null;

    private async Task ValidateValueAsync(RuleValueKind kind, string? value, CancellationToken ct)
    {
        value = value?.Trim();
        if (kind == RuleValueKind.None)
            return;
        if (string.IsNullOrEmpty(value))
            throw Invalid("Fill in the value of every condition and action");

        var ok = kind switch
        {
            RuleValueKind.Text => value.Length <= Conversations.Message.TextMaxLength,
            RuleValueKind.ChannelType => Enum.TryParse<Channel>(value, out var ch) && Enum.IsDefined(ch),
            RuleValueKind.Status => Enum.TryParse<TicketStatus>(value, out var st) && Enum.IsDefined(st),
            RuleValueKind.Boolean => value is "true" or "false",
            RuleValueKind.Site => Guid.TryParse(value, out var siteId) && await db.Sites.AnyAsync(s => s.Id == siteId, ct),
            // «none» — тикет никому не назначен.
            RuleValueKind.User => value == "none"
                                  || (Guid.TryParse(value, out var userId) && await db.Users.AnyAsync(u => u.Id == userId, ct)),
            RuleValueKind.Channel => await ChannelExistsAsync(value, ct),
            _ => false,
        };
        if (!ok)
            throw Invalid($"Value “{value}” is not valid here");
    }

    /// <summary>Канал задаётся как widget:&lt;id&gt; или telegram:&lt;id&gt;.</summary>
    private async Task<bool> ChannelExistsAsync(string value, CancellationToken ct)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id))
            return false;
        return parts[0] switch
        {
            "widget" => await db.Widgets.AnyAsync(w => w.Id == id, ct),
            "telegram" => await db.TelegramBots.AnyAsync(b => b.Id == id, ct),
            _ => false,
        };
    }

    private static List<string> Describe(Rule rule, string name, string? description, RuleInput input, RuleDefinition definition)
    {
        var changes = new List<string>();
        if (rule.Name != name)
            changes.Add($"Name: “{rule.Name}” → “{name}”");
        if (rule.Description != description)
            changes.Add("Description changed");
        if (rule.IsEnabled != input.IsEnabled)
            changes.Add(input.IsEnabled ? "Enabled" : "Disabled");
        if (rule.IsManual != input.IsManual)
            changes.Add(input.IsManual ? "Manual run on" : "Manual run off");
        if (rule.StopProcessing != input.StopProcessing)
            changes.Add(input.StopProcessing ? "Stops other rules" : "Does not stop other rules");
        if (rule.UseWordForms != input.UseWordForms || rule.IgnoreHtml != input.IgnoreHtml)
            changes.Add("Text matching options changed");
        if (!rule.Definition.WhenAll.SequenceEqual(definition.WhenAll) || !rule.Definition.WhenAny.SequenceEqual(definition.WhenAny))
            changes.Add("“When” changed");
        if (!rule.Definition.WhatAll.SequenceEqual(definition.WhatAll) || !rule.Definition.WhatAny.SequenceEqual(definition.WhatAny))
            changes.Add("“What” changed");
        if (!rule.Definition.Actions.SequenceEqual(definition.Actions))
            changes.Add("“Do” changed");
        return changes;
    }

    private async Task<string> UserNameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.FullName).SingleOrDefaultAsync(ct) ?? "—";

    private async Task<Rule> FindAsync(Guid id, CancellationToken ct) =>
        await db.Rules.SingleOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new DomainException(ErrorCodes.RuleNotFound, "Rule not found");

    private static DomainException Invalid(string message) => new(ErrorCodes.InvalidInput, message);

    private static RuleView ToView(Rule r, IReadOnlyList<RuleRevisionView> revisions) => new(
        r.Id, r.Name, r.Description, r.IsEnabled, r.IsManual, r.UseWordForms, r.IgnoreHtml, r.StopProcessing, r.Definition,
        r.CreatedAt, r.UpdatedAt, revisions);
}
