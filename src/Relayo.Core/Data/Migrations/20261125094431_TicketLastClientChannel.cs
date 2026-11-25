using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketLastClientChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_client_channel",
                table: "tickets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Widget");

            // Существующие тикеты: канал последнего сообщения клиента.
            migrationBuilder.Sql("""
                UPDATE tickets t
                SET last_client_channel = m.channel
                FROM (
                    SELECT DISTINCT ON (c.ticket_id) c.ticket_id, msg.channel
                    FROM messages msg
                    JOIN chats c ON c.id = msg.chat_id
                    WHERE msg.author = 'Client'
                    ORDER BY c.ticket_id, msg.created_at DESC
                ) m
                WHERE m.ticket_id = t.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_client_channel",
                table: "tickets");
        }
    }
}
