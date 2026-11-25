namespace Relayo.Core.Conversations;

public enum TicketStatus
{
    New,
    InProgress,
    WaitingClient,
    Closed,
}

/// <summary>
/// Обращение клиента. У тикета может быть несколько чатов, чат относится ровно к одному тикету (п. 3.2 ТЗ).
/// Например, второй чат появляется, когда посетитель с неподтверждённым clientId пишет
/// при уже открытом тикете этого клиента: его переписка не смешивается с чужой.
/// </summary>
public sealed class Ticket
{
    public const int PreviewMaxLength = 200;

    private Ticket()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    /// <summary>Номер для людей: «тикет #1024».</summary>
    public long Number { get; private set; }

    public Guid ClientId { get; private set; }

    public TicketStatus Status { get; private set; }

    public Guid? AssigneeId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Тикет закрыт, потому что его чаты перенесены в этот тикет.</summary>
    public Guid? MergedIntoId { get; private set; }

    public DateTimeOffset LastMessageAt { get; private set; }

    public string LastMessagePreview { get; private set; } = string.Empty;

    /// <summary>Канал, из которого клиент написал последним: значок в списке тикетов.</summary>
    public Channel LastClientChannel { get; private set; } = Channel.Widget;

    public List<Chat> Chats { get; private set; } = [];

    public bool IsOpen => Status != TicketStatus.Closed;

    public static Ticket Open(Guid clientId, DateTimeOffset now) => new()
    {
        ClientId = clientId,
        Status = TicketStatus.New,
        CreatedAt = now,
        UpdatedAt = now,
        LastMessageAt = now,
    };

    public void OnClientMessage(string text, Channel channel, DateTimeOffset now)
    {
        Status = AssigneeId is null ? TicketStatus.New : TicketStatus.InProgress;
        LastClientChannel = channel;
        Touch(text, now);
    }

    public void OnOperatorMessage(Guid operatorId, string text, DateTimeOffset now)
    {
        AssigneeId ??= operatorId;
        Status = TicketStatus.WaitingClient;
        Touch(text, now);
    }

    public void Assign(Guid operatorId, DateTimeOffset now)
    {
        AssigneeId = operatorId;
        if (Status == TicketStatus.New)
            Status = TicketStatus.InProgress;
        UpdatedAt = now;
    }

    /// <summary>Статус меняет правило автоматизации.</summary>
    public void SetStatus(TicketStatus status, DateTimeOffset now)
    {
        if (status == TicketStatus.Closed)
        {
            Close(now);
            return;
        }
        Status = status;
        ClosedAt = null;
        UpdatedAt = now;
    }

    /// <summary>Тикет возвращается в общую очередь.</summary>
    public void Unassign(DateTimeOffset now)
    {
        AssigneeId = null;
        if (Status == TicketStatus.InProgress)
            Status = TicketStatus.New;
        UpdatedAt = now;
    }

    public void Close(DateTimeOffset now)
    {
        Status = TicketStatus.Closed;
        ClosedAt = now;
        UpdatedAt = now;
    }

    public void CloseAsMergedInto(Guid ticketId, DateTimeOffset now)
    {
        MergedIntoId = ticketId;
        Close(now);
    }

    public void MoveToClient(Guid clientId) => ClientId = clientId;

    /// <summary>В тикет перенесены чаты другого тикета: превью берётся из более свежего.</summary>
    public void OnMergedFrom(Ticket other, DateTimeOffset now)
    {
        if (other.LastMessageAt > LastMessageAt)
        {
            LastMessageAt = other.LastMessageAt;
            LastMessagePreview = other.LastMessagePreview;
        }
        if (Status == TicketStatus.WaitingClient)
            Status = AssigneeId is null ? TicketStatus.New : TicketStatus.InProgress;
        UpdatedAt = now;
    }

    private void Touch(string text, DateTimeOffset now)
    {
        LastMessageAt = now;
        UpdatedAt = now;
        LastMessagePreview = text.Length <= PreviewMaxLength ? text : text[..PreviewMaxLength];
    }
}

/// <summary>Переписка внутри тикета: лента сообщений из всех каналов.</summary>
public sealed class Chat
{
    private Chat()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TicketId { get; private set; }

    public Ticket Ticket { get; private set; } = null!;

    public Guid ClientId { get; private set; }

    /// <summary>
    /// В чате писал посетитель с подтверждённой личностью. Такой чат видят все подтверждённые
    /// сессии клиента. Неподтверждённый чат видят только посетители, которые в нём писали.
    /// </summary>
    public bool IsVerified { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastMessageAt { get; private set; }

    public static Chat Start(Ticket ticket, bool verified, DateTimeOffset now)
    {
        var chat = new Chat
        {
            TicketId = ticket.Id,
            Ticket = ticket,
            ClientId = ticket.ClientId,
            IsVerified = verified,
            CreatedAt = now,
            LastMessageAt = now,
        };
        ticket.Chats.Add(chat);
        return chat;
    }

    public void MarkVerified() => IsVerified = true;

    public void MoveTo(Ticket ticket)
    {
        Ticket = ticket;
        TicketId = ticket.Id;
        ClientId = ticket.ClientId;
    }

    public void MoveToClient(Guid clientId) => ClientId = clientId;

    public void OnMessage(DateTimeOffset now) => LastMessageAt = now;
}

public enum MessageAuthor
{
    Client,
    Operator,
    System,
}

public enum Channel
{
    Widget,
    Telegram,
    WhatsApp,
}

/// <summary>
/// Сообщение. Таблица разбита на секции по месяцам (п. 5.1 ТЗ),
/// поэтому ключ составной: (Id, CreatedAt).
/// </summary>
public sealed class Message
{
    public const int TextMaxLength = 10_000;
    public const int ClientMessageIdMaxLength = 64;

    private Message()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid ChatId { get; private set; }

    public MessageAuthor Author { get; private set; }

    public Channel Channel { get; private set; }

    /// <summary>
    /// Идентичность клиента, от которой пришло сообщение: посетитель виджета или аккаунт Telegram.
    /// Название столбца осталось с тех пор, когда канал был один.
    /// </summary>
    public Guid? VisitorIdentityId { get; private set; }

    public Guid? OperatorId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    /// <summary>
    /// Id, который присвоил отправитель: виджет или Telegram (tg:&lt;message_id&gt;).
    /// Защищает от дублей при повторной отправке.
    /// </summary>
    public string? ClientMessageId { get; private set; }

    public static Message FromVisitor(Chat chat, Guid visitorIdentityId, string text, string? clientMessageId,
        DateTimeOffset now) => FromClient(chat, visitorIdentityId, Channel.Widget, text, clientMessageId, now);

    public static Message FromClient(Chat chat, Guid identityId, Channel channel, string text, string? clientMessageId,
        DateTimeOffset now) => new()
    {
        ChatId = chat.Id,
        Author = MessageAuthor.Client,
        Channel = channel,
        VisitorIdentityId = identityId,
        Text = text,
        ClientMessageId = clientMessageId,
        CreatedAt = now,
    };

    /// <summary>Событие в чате, например «клиент перешёл в Telegram». Привязано к идентичности, которой касается.</summary>
    public static Message System(Chat chat, Guid identityId, Channel channel, string text, DateTimeOffset now) => new()
    {
        ChatId = chat.Id,
        Author = MessageAuthor.System,
        Channel = channel,
        VisitorIdentityId = identityId,
        Text = text,
        CreatedAt = now,
    };

    /// <summary>
    /// Автоответ правила: автор — оператор без конкретного сотрудника. Клиент видит его от имени поддержки.
    /// </summary>
    public static Message FromAutomation(Chat chat, string text, Channel channel, DateTimeOffset now) => new()
    {
        ChatId = chat.Id,
        Author = MessageAuthor.Operator,
        Channel = channel,
        Text = text,
        CreatedAt = now,
    };

    public bool IsAutomated => Author == MessageAuthor.Operator && OperatorId is null;

    public static Message FromOperator(Chat chat, Guid operatorId, string text, Channel channel, DateTimeOffset now) => new()
    {
        ChatId = chat.Id,
        Author = MessageAuthor.Operator,
        Channel = channel,
        OperatorId = operatorId,
        Text = text,
        CreatedAt = now,
    };
}
