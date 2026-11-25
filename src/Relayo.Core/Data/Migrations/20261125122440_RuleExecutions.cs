using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RuleExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rule_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_number = table.Column<long>(type: "bigint", nullable: false),
                    trigger = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    applied = table.Column<bool>(type: "boolean", nullable: false),
                    details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_executions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rule_executions_created_at",
                table: "rule_executions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_rule_executions_ticket_id_created_at",
                table: "rule_executions",
                columns: new[] { "ticket_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rule_executions");
        }
    }
}
