namespace Relayo.Core.Users;

/// <summary>
/// Группа агентов — отдел (п. 3.1 ТЗ): по группам будут распределяться чаты. Сотрудник может состоять в нескольких.
/// </summary>
public sealed class AgentGroup
{
    public const int NameMaxLength = 100;

    private AgentGroup()
    {
    }

    public AgentGroup(string name, DateTimeOffset now)
    {
        Rename(name);
        CreatedAt = now;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>id группы в Usedesk (user_groups.id): по нему перенесённые сотрудники и тикеты находят группу.</summary>
    public int? UsedeskId { get; private set; }

    public void LinkUsedesk(int usedeskId) => UsedeskId = usedeskId;

    public void Rename(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > NameMaxLength)
            throw new DomainException(ErrorCodes.InvalidInput, $"Group name must be 1-{NameMaxLength} characters");
        Name = name;
    }
}
