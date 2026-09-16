using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InsuranceSample.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "policies",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Number = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                Premium = table.Column<decimal>(type: "numeric", nullable: false),
                EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_policies", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "policies_history",
            columns: table => new
            {
                history_id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                Id = table.Column<int>(type: "integer", nullable: true),
                Number = table.Column<string>(type: "text", nullable: true),
                Premium = table.Column<decimal>(type: "numeric", nullable: true),
                Status = table.Column<string>(type: "text", nullable: true),
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
                table.PrimaryKey("PK_policies_history", x => x.history_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_policies_history_version",
            table: "policies_history",
            columns: new[] { "Id", "valid_from" },
            descending: new[] { false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "policies");

        migrationBuilder.DropTable(
            name: "policies_history");
    }
}
