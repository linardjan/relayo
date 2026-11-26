using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsedeskImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "usedesk_channel_id",
                table: "widgets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usedesk_channel_id",
                table: "whatsapp_channels",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usedesk_channel_id",
                table: "telegram_bots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "usedesk_channel_id",
                table: "email_channels",
                type: "int",
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_widgets_usedesk_channel_id",
                table: "widgets",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_usedesk_channel_id",
                table: "whatsapp_channels",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_bots_usedesk_channel_id",
                table: "telegram_bots",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_email_channels_usedesk_channel_id",
                table: "email_channels",
                column: "usedesk_channel_id",
                unique: true,
                filter: "usedesk_channel_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usedesk_connection");

            migrationBuilder.DropIndex(
                name: "ix_widgets_usedesk_channel_id",
                table: "widgets");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_channels_usedesk_channel_id",
                table: "whatsapp_channels");

            migrationBuilder.DropIndex(
                name: "ix_telegram_bots_usedesk_channel_id",
                table: "telegram_bots");

            migrationBuilder.DropIndex(
                name: "ix_email_channels_usedesk_channel_id",
                table: "email_channels");

            migrationBuilder.DropColumn(
                name: "usedesk_channel_id",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "usedesk_channel_id",
                table: "whatsapp_channels");

            migrationBuilder.DropColumn(
                name: "usedesk_channel_id",
                table: "telegram_bots");

            migrationBuilder.DropColumn(
                name: "usedesk_channel_id",
                table: "email_channels");
        }
    }
}
