using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TaskTrackerSample.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "work_items",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Title = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                AssignedTo = table.Column<string>(type: "text", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_work_items", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "work_items_history",
            columns: table => new
            {
                history_id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                AssignedTo = table.Column<string>(type: "text", nullable: true),
                Id = table.Column<int>(type: "integer", nullable: true),
                Status = table.Column<string>(type: "text", nullable: true),
                Title = table.Column<string>(type: "text", nullable: true),
                changed_by = table.Column<string>(type: "text", nullable: true),
                changed_by_name = table.Column<string>(type: "text", nullable: true),
                correlation_id = table.Column<string>(type: "text", nullable: true),
                extra = table.Column<string>(type: "jsonb", nullable: true),
                operation = table.Column<short>(type: "smallint", nullable: false),
                reason = table.Column<string>(type: "text", nullable: true),
                valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                valid_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "'infinity'::timestamp with time zone")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_work_items_history", x => x.history_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_work_items_history_version",
            table: "work_items_history",
            columns: new[] { "Id", "valid_from" },
            descending: new[] { false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "work_items");

        migrationBuilder.DropTable(
            name: "work_items_history");
    }
}
