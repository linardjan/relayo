using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relayo.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidgetPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "public_key",
                table: "widgets",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "widgets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "published_version",
                table: "widgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "widgets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Существующим виджетам — случайный ключ и дата изменения, равная дате создания.
            migrationBuilder.Sql("""
                UPDATE widgets
                SET public_key = substr(replace(gen_random_uuid()::text, '-', ''), 1, 10),
                    updated_at = created_at;
                """);

            migrationBuilder.CreateTable(
                name: "widget_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    widget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    published_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_widget_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_widget_versions_widgets_widget_id",
                        column: x => x.widget_id,
                        principalTable: "widgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_widgets_public_key",
                table: "widgets",
                column: "public_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_widget_versions_widget_id_version",
                table: "widget_versions",
                columns: new[] { "widget_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "widget_versions");

            migrationBuilder.DropIndex(
                name: "ix_widgets_public_key",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "public_key",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "published_version",
                table: "widgets");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "widgets");
        }
    }
}
