using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "whatsapp_channel_id",
                table: "widgets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "whatsapp_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    phone_number = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false, collation: "Latin1_General_100_BIN2"),
                    site_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "ix_widgets_whatsapp_channel_id",
                table: "widgets",
                column: "whatsapp_channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_phone_number",
                table: "whatsapp_channels",
                column: "phone_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_channels_site_id",
                table: "whatsapp_channels",
                column: "site_id");

            migrationBuilder.AddForeignKey(
                name: "fk_widgets_whatsapp_channels_whatsapp_channel_id",
                table: "widgets",
                column: "whatsapp_channel_id",
                principalTable: "whatsapp_channels",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_widgets_whatsapp_channels_whatsapp_channel_id",
                table: "widgets");

            migrationBuilder.DropTable(
                name: "whatsapp_channels");

            migrationBuilder.DropIndex(
                name: "ix_widgets_whatsapp_channel_id",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "whatsapp_channel_id",
                table: "widgets");
        }
    }
}
