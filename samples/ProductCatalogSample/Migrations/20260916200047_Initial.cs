using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProductCatalogSample.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "products",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Sku = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Price = table.Column<decimal>(type: "numeric", nullable: false),
                StockQuantity = table.Column<int>(type: "integer", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_products", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "products_history",
            columns: table => new
            {
                history_id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                Id = table.Column<int>(type: "integer", nullable: true),
                Name = table.Column<string>(type: "text", nullable: true),
                Price = table.Column<decimal>(type: "numeric", nullable: true),
                Sku = table.Column<string>(type: "text", nullable: true),
                StockQuantity = table.Column<int>(type: "integer", nullable: true),
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
                table.PrimaryKey("PK_products_history", x => x.history_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_products_history_version",
            table: "products_history",
            columns: new[] { "Id", "valid_from" },
            descending: new[] { false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "products");

        migrationBuilder.DropTable(
            name: "products_history");
    }
}
