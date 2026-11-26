using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "ix_email_channels_site_id",
                table: "email_channels",
                column: "site_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_channels");
        }
    }
}
