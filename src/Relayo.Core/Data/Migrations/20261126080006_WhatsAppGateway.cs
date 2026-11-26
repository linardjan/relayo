using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_whatsapp_channels_phone_number",
                table: "whatsapp_channels");

            migrationBuilder.AlterColumn<string>(
                name: "phone_number",
                table: "whatsapp_channels",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(15)",
                oldMaxLength: 15,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "account_name",
                table: "whatsapp_channels",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "connected_at",
                table: "whatsapp_channels",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_connected",
                table: "whatsapp_channels",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "whatsapp_channels",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                // Каналы, созданные до подключения по QR, — «только ссылка».
                defaultValue: "LinkOnly");

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

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_phone_number",
                table: "whatsapp_channels",
                column: "phone_number");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_link_codes");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_channels_phone_number",
                table: "whatsapp_channels");

            migrationBuilder.DropColumn(
                name: "account_name",
                table: "whatsapp_channels");

            migrationBuilder.DropColumn(
                name: "connected_at",
                table: "whatsapp_channels");

            migrationBuilder.DropColumn(
                name: "is_connected",
                table: "whatsapp_channels");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "whatsapp_channels");

            migrationBuilder.AlterColumn<string>(
                name: "phone_number",
                table: "whatsapp_channels",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(15)",
                oldMaxLength: 15,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_phone_number",
                table: "whatsapp_channels",
                column: "phone_number",
                unique: true);
        }
    }
}
