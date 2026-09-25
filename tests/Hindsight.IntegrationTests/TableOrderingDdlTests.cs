using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// The statements <c>HindsightMigrationsSqlGenerator</c> adds for a new history table — the seed
/// <c>INSERT ... SELECT FROM &lt;main table&gt;</c> (both writers) and the trigger on the main table
/// (Trigger mode) — read the main table, so they can only run once the main table exists. EF Core sorts
/// <c>CreateTableOperation</c>s by foreign-key dependency, and a main table with any foreign key (here:
/// one to a lookup table and one to itself, a tree) is sorted after its history table, which has none.
/// These tests apply a fresh-database migration of exactly that model.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TableOrderingDdlTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task A_temporal_table_with_foreign_keys_migrates_a_fresh_database(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync($"fk_ordering_fresh_{writer}".ToLowerInvariant(), Ct);
        await using var db = Build(cs, writer);

        await ApplySchemaAsync(db, cs);

        var category = new Category { Code = "c" };
        var parent = new Node { Name = "root", Category = category };
        db.Nodes.Add(parent);
        db.Nodes.Add(new Node { Name = "child", Parent = parent, Category = category });
        await db.SaveChangesAsync(Ct);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from nodes_history where valid_to = 'infinity'";
        Assert.Equal(2L, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task The_seed_and_trigger_follow_the_main_tables_create_table()
    {
        await using var db = Build("Host=unused", HistoryWriter.Trigger);
        var model = db.GetService<IDesignTimeModel>().Model;
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var sql = string.Join(
            "\n\n",
            db.GetService<IMigrationsSqlGenerator>().Generate(operations, model).Select(command => command.CommandText));

        await Verify(sql, extension: "sql");
    }

    private static async Task ApplySchemaAsync(NodeContext db, string connectionString)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations, model);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = command.CommandText;
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    private static NodeContext Build(string connectionString, HistoryWriter writer)
    {
        var options = new DbContextOptionsBuilder<NodeContext>()
            .UseNpgsql(connectionString)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        return new NodeContext(options);
    }

    private sealed class Category
    {
        public int Id { get; set; }

        public string Code { get; set; } = "";
    }

    private sealed class Node
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public int CategoryId { get; set; }

        public Category? Category { get; set; }

        public int? ParentId { get; set; }

        public Node? Parent { get; set; }
    }

    private sealed class NodeContext(DbContextOptions<NodeContext> options) : DbContext(options)
    {
        public DbSet<Node> Nodes => Set<Node>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var category = modelBuilder.Entity<Category>();
            category.ToTable("categories");
            category.Property(c => c.Id).HasColumnName("id");
            category.Property(c => c.Code).HasColumnName("code");

            var node = modelBuilder.Entity<Node>();
            node.ToTable("nodes");
            node.Property(n => n.Id).HasColumnName("id");
            node.Property(n => n.Name).HasColumnName("name");
            node.Property(n => n.CategoryId).HasColumnName("category_id");
            node.Property(n => n.ParentId).HasColumnName("parent_id");
            node.HasOne(n => n.Category).WithMany().HasForeignKey(n => n.CategoryId);
            node.HasOne(n => n.Parent).WithMany().HasForeignKey(n => n.ParentId);
            node.IsTemporal();
        }
    }
}
