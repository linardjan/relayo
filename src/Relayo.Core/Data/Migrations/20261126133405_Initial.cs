using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Одна миграция вместо шести (до первого выпуска). Значения по умолчанию у whatsapp_channels.mode, .is_connected,
            // tickets.source, messages.visibility, clients.is_vip и .is_spammer есть только в БД (не в модели): на них рассчитывает
            // массовая запись перенесённой истории.
            migrationBuilder.CreateTable(
                name: "agent_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ldap_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    host = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    port = table.Column<int>(type: "int", nullable: false),
                    security = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    verify_certificate = table.Column<bool>(type: "bit", nullable: false),
                    provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    bind_dn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    protected_bind_password = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    users_dn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    guid_attribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    login_attribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    full_name_attribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    mail_attribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    phone_attribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    position_attribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    default_role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    sync_interval_minutes = table.Column<int>(type: "int", nullable: false),
                    last_check_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_check_ok = table.Column<bool>(type: "bit", nullable: true),
                    last_check_message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_sync_ok = table.Column<bool>(type: "bit", nullable: true),
                    last_sync_message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ldap_settings", x => x.id);
                    table.CheckConstraint("ck_ldap_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "rule_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ticket_number = table.Column<long>(type: "bigint", nullable: false),
                    trigger = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    applied = table.Column<bool>(type: "bit", nullable: false),
                    details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_executions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    is_manual = table.Column<bool>(type: "bit", nullable: false),
                    use_word_forms = table.Column<bool>(type: "bit", nullable: false),
                    ignore_html = table.Column<bool>(type: "bit", nullable: false),
                    stop_processing = table.Column<bool>(type: "bit", nullable: false),
                    position = table.Column<int>(type: "int", nullable: false),
                    definition = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    allowed_origins = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usedesk_connection",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false),
                    host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "int", nullable: false),
                    database = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    protected_password = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    company_id = table.Column<int>(type: "int", nullable: false),
                    last_check_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_check_ok = table.Column<bool>(type: "bit", nullable: true),
                    last_check_message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usedesk_connection", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    login = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    login_normalized = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    full_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    alias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ad_object_guid = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    password_hash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    is_blocked = table.Column<bool>(type: "bit", nullable: false),
                    email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    position = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    default_group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    group_ids = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    blocked_by_directory = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_agent_groups_default_group_id",
                        column: x => x.default_group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    external_client_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    is_verified = table.Column<bool>(type: "bit", nullable: false),
                    is_vip = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    is_spammer = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    position = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    avatar_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true, collation: "Latin1_General_100_BIN2"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clients", x => x.id);
                    table.ForeignKey(
                        name: "fk_clients_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "email_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    forward_creates_ticket = table.Column<bool>(type: "bit", nullable: false),
                    quoting = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    last_check_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_check_ok = table.Column<bool>(type: "bit", nullable: true),
                    last_check_message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_channel_id = table.Column<int>(type: "int", nullable: true),
                    imap_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    imap_host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    imap_port = table.Column<int>(type: "int", nullable: false),
                    imap_protected_password = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    imap_security = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    imap_username = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    smtp_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    smtp_host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    smtp_port = table.Column<int>(type: "int", nullable: false),
                    smtp_protected_password = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    smtp_security = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    smtp_username = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_channels_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "site_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    protected_secret = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_site_keys_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "telegram_bots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    protected_token = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    protected_webhook_secret = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_channel_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_bots", x => x.id);
                    table.ForeignKey(
                        name: "fk_telegram_bots_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "LinkOnly"),
                    phone_number = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true, collation: "Latin1_General_100_BIN2"),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    is_connected = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    account_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    connected_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_channel_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_whatsapp_channels_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rule_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rule_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    user_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_rule_revisions_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_rule_revisions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "client_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    client_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    external_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    channel_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    linked_from_identity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    is_verified = table.Column<bool>(type: "bit", nullable: false),
                    widget_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_identities", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_identities_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    client_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    assignee_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    merged_into_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_message_preview = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    last_client_channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    subject = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "Relayo"),
                    source_channel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_tickets_agent_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_tickets_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "widgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    public_key = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, collation: "Latin1_General_100_BIN2"),
                    published_version = table.Column<int>(type: "int", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    identification_mode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    greeting = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    accent_color = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    position = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    default_locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    usedesk_channel_id = table.Column<int>(type: "int", nullable: true),
                    telegram_bot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    whatsapp_channel_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_widgets", x => x.id);
                    table.ForeignKey(
                        name: "fk_widgets_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_widgets_telegram_bots_telegram_bot_id",
                        column: x => x.telegram_bot_id,
                        principalTable: "telegram_bots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_widgets_whatsapp_channels_whatsapp_channel_id",
                        column: x => x.whatsapp_channel_id,
                        principalTable: "whatsapp_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "channel_link_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    bot_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    visitor_identity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_link_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_link_tokens_client_identities_visitor_identity_id",
                        column: x => x.visitor_identity_id,
                        principalTable: "client_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_channel_link_tokens_telegram_bots_bot_id",
                        column: x => x.bot_id,
                        principalTable: "telegram_bots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_link_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    channel_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    visitor_identity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_link_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_whatsapp_link_codes_client_identities_visitor_identity_id",
                        column: x => x.visitor_identity_id,
                        principalTable: "client_identities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_whatsapp_link_codes_whatsapp_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "whatsapp_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    client_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    is_verified = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    usedesk_id = table.Column<long>(type: "bigint", nullable: true),
                    page_url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chats", x => x.id);
                    table.ForeignKey(
                        name: "fk_chats_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chats_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "widget_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    widget_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    snapshot_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    published_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_widget_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_widget_versions_widgets_widget_id",
                        column: x => x.widget_id,
                        principalTable: "widgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    chat_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_attachments_chats_chat_id",
                        column: x => x.chat_id,
                        principalTable: "chats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    chat_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    author = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    visitor_identity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    operator_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    text = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    client_message_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    visibility = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "Public"),
                    usedesk_id = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => new { x.created_at, x.id });
                    table.ForeignKey(
                        name: "fk_messages_chats_chat_id",
                        column: x => x.chat_id,
                        principalTable: "chats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Сообщения секционированы по месяцам (п. 5.1 ТЗ): кластерный ключ переносится на схему
            // секционирования по created_at. Индексы ниже создаются после этого и тоже делятся по секциям.
            // Первая граница — текущий месяц, остальные создаёт MessagePartitions при запуске сервиса.
            var now = DateTime.UtcNow;
            migrationBuilder.Sql(MessagePartitions.CreateSql(new DateOnly(now.Year, now.Month, 1)));
            migrationBuilder.Sql($"""
                CREATE UNIQUE CLUSTERED INDEX pk_messages ON messages (created_at, id)
                WITH (DROP_EXISTING = ON) ON {MessagePartitions.Scheme}(created_at);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_agent_groups_name",
                table: "agent_groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_groups_usedesk_id",
                table: "agent_groups",
                column: "usedesk_id",
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_chat_id_created_at",
                table: "attachments",
                columns: new[] { "chat_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_message_id",
                table: "attachments",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_link_tokens_bot_id",
                table: "channel_link_tokens",
                column: "bot_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_link_tokens_expires_at",
                table: "channel_link_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_channel_link_tokens_token_hash",
                table: "channel_link_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_channel_link_tokens_visitor_identity_id",
                table: "channel_link_tokens",
                column: "visitor_identity_id");

            migrationBuilder.CreateIndex(
                name: "ix_chats_client_id_last_message_at",
                table: "chats",
                columns: new[] { "client_id", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chats_ticket_id",
                table: "chats",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_chats_usedesk_id",
                table: "chats",
                column: "usedesk_id",
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_client_identities_client_id",
                table: "client_identities",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_identities_linked_from_identity_id",
                table: "client_identities",
                column: "linked_from_identity_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_identities_type_external_id",
                table: "client_identities",
                columns: new[] { "type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clients_site_id_external_client_id",
                table: "clients",
                columns: new[] { "site_id", "external_client_id" },
                unique: true,
                filter: "external_client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_clients_usedesk_id_site_id",
                table: "clients",
                columns: new[] { "usedesk_id", "site_id" },
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_email_channels_site_id",
                table: "email_channels",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_channels_usedesk_channel_id",
                table: "email_channels",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_chat_id_created_at",
                table: "messages",
                columns: new[] { "chat_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_id",
                table: "messages",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_operator_id",
                table: "messages",
                column: "operator_id",
                filter: "operator_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_usedesk_id_created_at",
                table: "messages",
                columns: new[] { "usedesk_id", "created_at" },
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_messages_visitor_identity_id_created_at",
                table: "messages",
                columns: new[] { "visitor_identity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_rule_executions_created_at",
                table: "rule_executions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_rule_executions_ticket_id_created_at",
                table: "rule_executions",
                columns: new[] { "ticket_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_revisions_rule_id_created_at",
                table: "rule_revisions",
                columns: new[] { "rule_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_revisions_user_id",
                table: "rule_revisions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_rules_position",
                table: "rules",
                column: "position");

            migrationBuilder.CreateIndex(
                name: "ix_site_keys_key_id",
                table: "site_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_keys_site_id",
                table: "site_keys",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_sites_code",
                table: "sites",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telegram_bots_site_id",
                table: "telegram_bots",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_bots_telegram_id",
                table: "telegram_bots",
                column: "telegram_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telegram_bots_usedesk_channel_id",
                table: "telegram_bots",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_client_id_status",
                table: "tickets",
                columns: new[] { "client_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_group_id",
                table: "tickets",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_number",
                table: "tickets",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_status_last_message_at",
                table: "tickets",
                columns: new[] { "status", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_users_ad_object_guid",
                table: "users",
                column: "ad_object_guid",
                unique: true,
                filter: "[ad_object_guid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_default_group_id",
                table: "users",
                column: "default_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_login_normalized",
                table: "users",
                column: "login_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_usedesk_id",
                table: "users",
                column: "usedesk_id",
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_phone_number",
                table: "whatsapp_channels",
                column: "phone_number");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_site_id",
                table: "whatsapp_channels",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_usedesk_channel_id",
                table: "whatsapp_channels",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_link_codes_channel_id",
                table: "whatsapp_link_codes",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_link_codes_code_hash",
                table: "whatsapp_link_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_link_codes_expires_at",
                table: "whatsapp_link_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_link_codes_visitor_identity_id",
                table: "whatsapp_link_codes",
                column: "visitor_identity_id");

            migrationBuilder.CreateIndex(
                name: "ix_widget_versions_widget_id_version",
                table: "widget_versions",
                columns: new[] { "widget_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_widgets_public_key",
                table: "widgets",
                column: "public_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_widgets_site_id",
                table: "widgets",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_widgets_telegram_bot_id",
                table: "widgets",
                column: "telegram_bot_id");

            migrationBuilder.CreateIndex(
                name: "ix_widgets_usedesk_channel_id",
                table: "widgets",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_widgets_whatsapp_channel_id",
                table: "widgets",
                column: "whatsapp_channel_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "channel_link_tokens");

            migrationBuilder.DropTable(
                name: "email_channels");

            migrationBuilder.DropTable(
                name: "ldap_settings");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.Sql($"DROP PARTITION SCHEME {MessagePartitions.Scheme}; DROP PARTITION FUNCTION {MessagePartitions.Function};");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "rule_executions");

            migrationBuilder.DropTable(
                name: "rule_revisions");

            migrationBuilder.DropTable(
                name: "site_keys");

            migrationBuilder.DropTable(
                name: "usedesk_connection");

            migrationBuilder.DropTable(
                name: "whatsapp_link_codes");

            migrationBuilder.DropTable(
                name: "widget_versions");

            migrationBuilder.DropTable(
                name: "chats");

            migrationBuilder.DropTable(
                name: "rules");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "client_identities");

            migrationBuilder.DropTable(
                name: "widgets");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropTable(
                name: "telegram_bots");

            migrationBuilder.DropTable(
                name: "whatsapp_channels");

            migrationBuilder.DropTable(
                name: "agent_groups");

            migrationBuilder.DropTable(
                name: "clients");

            migrationBuilder.DropTable(
                name: "sites");
        }
    }
}
