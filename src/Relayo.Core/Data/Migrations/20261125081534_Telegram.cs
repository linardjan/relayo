using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Telegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "telegram_bot_id",
                table: "widgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "clients",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "channel_id",
                table: "client_identities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "client_identities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "linked_from_identity_id",
                table: "client_identities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "telegram_bots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    telegram_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protected_token = table.Column<string>(type: "text", nullable: false),
                    protected_webhook_secret = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "channel_link_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visitor_identity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "ix_widgets_telegram_bot_id",
                table: "widgets",
                column: "telegram_bot_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_identities_linked_from_identity_id",
                table: "client_identities",
                column: "linked_from_identity_id");

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
                name: "ix_telegram_bots_site_id",
                table: "telegram_bots",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_bots_telegram_id",
                table: "telegram_bots",
                column: "telegram_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_widgets_telegram_bots_telegram_bot_id",
                table: "widgets",
                column: "telegram_bot_id",
                principalTable: "telegram_bots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_widgets_telegram_bots_telegram_bot_id",
                table: "widgets");

            migrationBuilder.DropTable(
                name: "channel_link_tokens");

            migrationBuilder.DropTable(
                name: "telegram_bots");

            migrationBuilder.DropIndex(
                name: "ix_widgets_telegram_bot_id",
                table: "widgets");

            migrationBuilder.DropIndex(
                name: "ix_client_identities_linked_from_identity_id",
                table: "client_identities");

            migrationBuilder.DropColumn(
                name: "telegram_bot_id",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "channel_id",
                table: "client_identities");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "client_identities");

            migrationBuilder.DropColumn(
                name: "linked_from_identity_id",
                table: "client_identities");
        }
    }
}
