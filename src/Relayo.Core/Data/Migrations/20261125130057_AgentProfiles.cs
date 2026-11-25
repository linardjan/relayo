using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_group_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "group_ids",
                table: "users",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "locale",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "en");

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "position",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Offline");

            migrationBuilder.AddColumn<string>(
                name: "mail_attribute",
                table: "ldap_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone_attribute",
                table: "ldap_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "position_attribute",
                table: "ldap_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_groups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_users_default_group_id",
                table: "users",
                column: "default_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_operator_id",
                table: "messages",
                column: "operator_id",
                filter: "operator_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_agent_groups_name",
                table: "agent_groups",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_users_agent_groups_default_group_id",
                table: "users",
                column: "default_group_id",
                principalTable: "agent_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Уже настроенный AD: стандартные атрибуты почты, телефона и должности.
            migrationBuilder.Sql("UPDATE ldap_settings SET mail_attribute = 'mail', phone_attribute = 'telephoneNumber', position_attribute = 'title';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_agent_groups_default_group_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "agent_groups");

            migrationBuilder.DropIndex(
                name: "ix_users_default_group_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_messages_operator_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "default_group_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "group_ids",
                table: "users");

            migrationBuilder.DropColumn(
                name: "locale",
                table: "users");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "users");

            migrationBuilder.DropColumn(
                name: "position",
                table: "users");

            migrationBuilder.DropColumn(
                name: "status",
                table: "users");

            migrationBuilder.DropColumn(
                name: "mail_attribute",
                table: "ldap_settings");

            migrationBuilder.DropColumn(
                name: "phone_attribute",
                table: "ldap_settings");

            migrationBuilder.DropColumn(
                name: "position_attribute",
                table: "ldap_settings");
        }
    }
}
