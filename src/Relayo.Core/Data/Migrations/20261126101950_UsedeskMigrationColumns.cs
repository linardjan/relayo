using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsedeskMigrationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Столбцы для переноса из Usedesk (docs/usedesk-migration.md, раздел 3). Существующие тикеты созданы
            // в Relayo, существующие сообщения видны клиентам — значения по умолчанию заполняют старые строки.
            migrationBuilder.AddColumn<int>(
                name: "usedesk_id",
                table: "users",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "tickets",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Relayo");

            migrationBuilder.AddColumn<string>(
                name: "source_channel",
                table: "tickets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject",
                table: "tickets",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usedesk_id",
                table: "messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "visibility",
                table: "messages",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.AddColumn<long>(
                name: "usedesk_id",
                table: "clients",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "page_url",
                table: "chats",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "usedesk_id",
                table: "chats",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_usedesk_id",
                table: "users",
                column: "usedesk_id",
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_group_id",
                table: "tickets",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_usedesk_id_created_at",
                table: "messages",
                columns: new[] { "usedesk_id", "created_at" },
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_clients_usedesk_id",
                table: "clients",
                column: "usedesk_id",
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_chats_usedesk_id",
                table: "chats",
                column: "usedesk_id",
                unique: true,
                filter: "usedesk_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_agent_groups_group_id",
                table: "tickets",
                column: "group_id",
                principalTable: "agent_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_agent_groups_group_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_users_usedesk_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_tickets_group_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_messages_usedesk_id_created_at",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_clients_usedesk_id",
                table: "clients");

            migrationBuilder.DropIndex(
                name: "ix_chats_usedesk_id",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "usedesk_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "source",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "source_channel",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "subject",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "usedesk_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "usedesk_id",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "page_url",
                table: "chats");

            migrationBuilder.DropColumn(
                name: "usedesk_id",
                table: "chats");
        }
    }
}
