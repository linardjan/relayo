using System.Data.Common;
using MySqlConnector;
using Relayo.Core;

namespace Relayo.Internal.Usedesk;

/// <summary>Сотрудник Usedesk (users). <paramref name="Guid"/> — objectGUID из users_guid_migration, если нашёлся.</summary>
public sealed record UsedeskUser(int Id, string Email, string Name, string? Position, string? Phone, string Role, string Lang,
    bool Deleted, bool Active, string? Guid);

/// <summary>Канал Usedesk (company_email_channels): общая таблица каналов всех видов, а у почты — ещё и подключения.</summary>
public sealed record UsedeskChannel(
    int Id,
    string Name,
    string? Type,
    string? SubType,
    bool Deleted,
    string IncomingConnection,
    string? ImapHost,
    int? ImapPort,
    string? ImapUsername,
    string? ImapPassword,
    string? ImapEncrypted,
    string? FromEmail,
    string? MainEmail,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    string? SmtpPassword,
    string? SmtpEncrypted,
    bool Unencrypted,
    bool Quotes);

/// <summary>Telegram-бот Usedesk (telegram_settings). Токен — открытым текстом: не логировать и не отдавать наружу.</summary>
public sealed record UsedeskTelegram(int Id, int? ChannelId, string? Token);

/// <summary>Подключение Pact (pact_settings): через него в Usedesk работал WhatsApp.</summary>
public sealed record UsedeskPact(int Id, int? ChannelId, string? ProviderType, string? AccountName, bool Active, bool GroupChats);

public sealed record UsedeskWidget(
    int Id,
    int? ChannelId,
    string? Name,
    string? Title,
    string Color,
    bool Attachment,
    int ZIndex,
    string Language,
    string? BadgeText,
    string? BadgeTextColor,
    string? BadgeColor,
    int BadgeFontSize,
    string Alignment,
    string? ButtonColor,
    string? OperatorName,
    int ShowTooltipType,
    bool HideOnOutsideClick,
    bool TopicsRequired,
    bool CallbackEnabled,
    bool AllowRating,
    string? CustomFields);

/// <summary>Пункты виджета (messenger_settings): active_json и ссылки на соцсети.</summary>
public sealed record UsedeskMessenger(int? ChannelId, string? ActiveJson, string? FacebookUrl, string? VkUrl, string? ViberUrl,
    string? TgUrl, string? OkUrl, string? InstaUrl);

/// <summary>Всё, что импорт настроек читает из Usedesk: записей немного, читаются разом.</summary>
public sealed record UsedeskSnapshot(
    IReadOnlyList<UsedeskUser> Users,
    IReadOnlyList<UsedeskChannel> Channels,
    IReadOnlyList<UsedeskTelegram> Telegram,
    IReadOnlyList<UsedeskPact> Pact,
    IReadOnlyList<UsedeskWidget> Widgets,
    IReadOnlyList<UsedeskMessenger> Messengers,
    bool GuidTableFound);

public sealed record UsedeskLogin(string Host, int Port, string Database, string Username, string Password, int CompanyId);

/// <summary>
/// Чтение базы Usedesk (MySQL). Только SELECT, только записи нашей компании (company_id). Хватает пользователя MySQL
/// с правом чтения.
/// </summary>
public sealed class UsedeskSource
{
    private const int TableNotFound = 1146;

    public static MySqlConnection Connect(UsedeskLogin login) => new(new MySqlConnectionStringBuilder
    {
        Server = login.Host,
        Port = (uint)login.Port,
        Database = login.Database,
        UserID = login.Username,
        Password = login.Password,
        SslMode = MySqlSslMode.Preferred,
        ConnectionTimeout = 10,
        DefaultCommandTimeout = 60,
        // Нулевые даты MySQL (0000-00-00) читаются как DateTime.MinValue, а не ошибкой.
        ConvertZeroDateTime = true,
        Pooling = false,
        CharacterSet = "utf8mb4",
    }.ConnectionString);

    /// <summary>Проверка подключения: версия сервера и сколько записей нашей компании видно.</summary>
    public async Task<string> CheckAsync(UsedeskLogin login, CancellationToken ct)
    {
        await using var db = await OpenAsync(login, ct);
        var version = db.ServerVersion;
        var users = await CountAsync(db, "SELECT COUNT(*) FROM users WHERE company_id = @company", login.CompanyId, ct);
        var channels = await CountAsync(db, "SELECT COUNT(*) FROM company_email_channels WHERE company_id = @company", login.CompanyId, ct);
        var widgets = await CountAsync(db, "SELECT COUNT(*) FROM usedesk_widget WHERE company_id = @company", login.CompanyId, ct);
        if (users == 0 && channels == 0)
            throw new DomainException(ErrorCodes.InvalidInput,
                $"Connected to MySQL {version}, but company {login.CompanyId} has no users and channels. Check the company id");
        return $"Connected to MySQL {version}. Company {login.CompanyId}: users {users}, channels {channels}, widgets {widgets}.";
    }

    public async Task<UsedeskSnapshot> ReadAsync(UsedeskLogin login, CancellationToken ct)
    {
        await using var db = await OpenAsync(login, ct);
        var company = login.CompanyId;

        var users = await ListAsync(db, """
            SELECT id, email, name, position, phone, role, lang, deleted, status
            FROM users WHERE company_id = @company ORDER BY id
            """, company, r => new UsedeskUser(Int(r, "id"), Str(r, "email") ?? "", Str(r, "name") ?? "", Str(r, "position"),
            Str(r, "phone"), Str(r, "role") ?? "", Str(r, "lang") ?? "ru", Bool(r, "deleted"), Bool(r, "status"), null), ct);

        // objectGUID из LDAP — в отдельной таблице: в users.guid его очистили (docs/usedesk-migration.md, 2.1).
        var guidTableFound = true;
        try
        {
            var guids = await ListAsync(db, """
                SELECT g.original_id, g.duplicate_guid FROM users_guid_migration g
                JOIN users u ON u.id = g.original_id WHERE u.company_id = @company
                """, company, r => (Id: Int(r, "original_id"), Guid: Str(r, "duplicate_guid")), ct);
            var byUser = guids.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Guid);
            users = users.Select(u => byUser.TryGetValue(u.Id, out var guid) ? u with { Guid = guid } : u).ToList();
        }
        catch (MySqlException ex) when (ex.Number == TableNotFound)
        {
            guidTableFound = false;
        }

        var channels = await ListAsync(db, """
            SELECT id, name, type, sub_type, deleted, incoming_connection, imap_host, imap_port, imap_username, imap_password,
                   imap_encrypted, from_email, main_email, smtp_host, smtp_port, smtp_username, smtp_password, smtp_encrypted,
                   unencrypted_connection, quotes
            FROM company_email_channels WHERE company_id = @company ORDER BY id
            """, company, r => new UsedeskChannel(Int(r, "id"), Str(r, "name") ?? "", Str(r, "type"), Str(r, "sub_type"),
            Bool(r, "deleted"), Str(r, "incoming_connection") ?? "", Str(r, "imap_host"), IntOrNull(r, "imap_port"),
            Str(r, "imap_username"), Str(r, "imap_password"), Str(r, "imap_encrypted"), Str(r, "from_email"), Str(r, "main_email"),
            Str(r, "smtp_host"), IntOrNull(r, "smtp_port"), Str(r, "smtp_username"), Str(r, "smtp_password"),
            Str(r, "smtp_encrypted"), Bool(r, "unencrypted_connection"), Bool(r, "quotes")), ct);

        var telegram = await ListAsync(db, "SELECT id, token, channel_id FROM telegram_settings WHERE company_id = @company ORDER BY id",
            company, r => new UsedeskTelegram(Int(r, "id"),
                int.TryParse(Str(r, "channel_id"), out var channelId) ? channelId : null, Str(r, "token")), ct);

        var pact = await ListAsync(db, """
            SELECT id, channel_id, provider_type, account_name, is_active, group_chats
            FROM pact_settings WHERE company_id = @company AND deleted_at IS NULL ORDER BY id
            """, company, r => new UsedeskPact(Int(r, "id"), IntOrNull(r, "channel_id"), Str(r, "provider_type"),
            Str(r, "account_name"), Bool(r, "is_active"), Bool(r, "group_chats")), ct);

        var widgets = await ListAsync(db, """
            SELECT id, company_email_channel_id, name, title, color, attachment, z_index, language, badge_text, badge_text_color,
                   badge_color, badge_font_size, aligment, button_color, operator_name, show_tooltip_type,
                   hide_when_clicked_outside, topics_required, chat_callback_enabled, allow_rating, custom_fields
            FROM usedesk_widget WHERE company_id = @company ORDER BY id
            """, company, r => new UsedeskWidget(Int(r, "id"), IntOrNull(r, "company_email_channel_id"), Str(r, "name"),
            Str(r, "title"), Str(r, "color") ?? "", Bool(r, "attachment"), Int(r, "z_index"), Str(r, "language") ?? "",
            Str(r, "badge_text"), Str(r, "badge_text_color"), Str(r, "badge_color"), Int(r, "badge_font_size"),
            Str(r, "aligment") ?? "right", Str(r, "button_color"), Str(r, "operator_name"), Int(r, "show_tooltip_type"),
            Bool(r, "hide_when_clicked_outside"), Bool(r, "topics_required"), Bool(r, "chat_callback_enabled"),
            Bool(r, "allow_rating"), Str(r, "custom_fields")), ct);

        var messengers = await ListAsync(db, """
            SELECT channel_id, active_json, facebook_url, vk_url, viber_url, tg_url, ok_url, insta_url
            FROM messenger_settings WHERE company_id = @company ORDER BY id
            """, company, r => new UsedeskMessenger(IntOrNull(r, "channel_id"), Str(r, "active_json"), Str(r, "facebook_url"),
            Str(r, "vk_url"), Str(r, "viber_url"), Str(r, "tg_url"), Str(r, "ok_url"), Str(r, "insta_url")), ct);

        return new UsedeskSnapshot(users, channels, telegram, pact, widgets, messengers, guidTableFound);
    }

    private static async Task<MySqlConnection> OpenAsync(UsedeskLogin login, CancellationToken ct)
    {
        var db = Connect(login);
        try
        {
            await db.OpenAsync(ct);
            return db;
        }
        catch (MySqlException ex)
        {
            await db.DisposeAsync();
            throw new DomainException(ErrorCodes.UsedeskUnavailable, $"Could not connect to the Usedesk database: {ex.Message}");
        }
    }

    private static async Task<long> CountAsync(MySqlConnection db, string sql, int company, CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, db);
        command.Parameters.AddWithValue("@company", company);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<List<T>> ListAsync<T>(MySqlConnection db, string sql, int company, Func<DbDataReader, T> map,
        CancellationToken ct)
    {
        await using var command = new MySqlCommand(sql, db);
        command.Parameters.AddWithValue("@company", company);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
            list.Add(map(reader));
        return list;
    }

    private static string? Str(DbDataReader r, string column)
    {
        var value = r[column];
        return value is DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int? IntOrNull(DbDataReader r, string column) => r[column] is DBNull ? null : Convert.ToInt32(r[column]);

    private static int Int(DbDataReader r, string column) => IntOrNull(r, column) ?? 0;

    /// <summary>tinyint(1) приходит как bool, int — как число: оба сводятся к «не ноль».</summary>
    private static bool Bool(DbDataReader r, string column) => r[column] switch
    {
        DBNull => false,
        bool b => b,
        var v => Convert.ToInt64(v) != 0,
    };
}
