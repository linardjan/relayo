using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Directory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "blocked_by_directory",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ldap_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    host = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    security = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    verify_certificate = table.Column<bool>(type: "boolean", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    bind_dn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    protected_bind_password = table.Column<string>(type: "text", nullable: true),
                    users_dn = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    guid_attribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    login_attribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    full_name_attribute = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    default_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sync_interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    last_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_check_ok = table.Column<bool>(type: "boolean", nullable: true),
                    last_check_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_ok = table.Column<bool>(type: "boolean", nullable: true),
                    last_sync_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ldap_settings", x => x.id);
                    table.CheckConstraint("ck_ldap_settings_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ldap_settings");

            migrationBuilder.DropColumn(
                name: "blocked_by_directory",
                table: "users");
        }
    }
}
