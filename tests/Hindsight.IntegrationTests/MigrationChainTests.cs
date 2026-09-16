using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// Every other schema-evolution test in this project builds exactly two models (v1, v2) and diffs them
/// once. Real usage is a chain: <c>dotnet ef migrations add</c> runs repeatedly over a project's
/// lifetime, and each new migration's snapshot is built from the *previous migration's* compiled
/// <c>ModelSnapshot</c>, not from the very first one. This file applies three migrations in sequence —
/// each one's generated DDL against the database the previous one actually left behind, exactly as
/// <c>dotnet ef database update</c> would — starting from an empty database, and asserts the final
/// schema rather than any single step's diff in isolation. It deliberately chains together several of
/// DESIGN.md D6's scenarios (add, remove, rename-the-table) so a regression that only shows up once
/// orphaned state has accumulated across more than one migration would be caught here even if each
/// individual transition passed on its own elsewhere in this project.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrationChainTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Three_migrations_applied_in_sequence_to_an_empty_database_leave_the_expected_final_schema()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Three_migrations_applied_in_sequence_to_an_empty_database_leave_the_expected_final_schema), Ct);

        // Migration A: Policy(id, number, legacy_field), temporal, table "policies". Applied to an empty
        // database — no prior snapshot.
        await using var a = new PolicyContext(cs, PolicyShape.A);
        var aModel = a.GetService<IDesignTimeModel>().Model;
        await ApplyAsync(a, cs, previousModel: null);

        a.Policies.Add(new Policy { Id = 1, Number = "P-1", LegacyField = "keep-me" });
        await a.SaveChangesAsync(Ct);

        await AssertSchemaAsync(
            cs,
            mainTable: "policies",
            mainColumns: ["id", "number", "legacy_field"],
            historyTable: "policies_history",
            historyColumns: ["id", "number", "legacy_field"]);

        // Migration B, built from A's snapshot: adds "premium", removes "legacy_field". Applied on top of
        // whatever A actually left in the database.
        await using var b = new PolicyContext(cs, PolicyShape.B, snapshotModel: aModel);
        var bModel = b.GetService<IDesignTimeModel>().Model;
        await ApplyAsync(b, cs, previousModel: aModel);

        await AssertSchemaAsync(
            cs,
            mainTable: "policies",
            mainColumns: ["id", "number", "premium"],
            historyTable: "policies_history",
            historyColumns: ["id", "number", "premium", "legacy_field"]);

        var legacyStillThere = await ScalarAsync(
            cs, "select legacy_field from policies_history where id = 1");
        Assert.Equal("keep-me", legacyStillThere);

        b.Policies.Add(new Policy { Id = 2, Number = "P-2", Premium = 100m });
        await b.SaveChangesAsync(Ct);

        // Migration C, built from B's snapshot: renames the main table to "contracts" (default-suffix
        // history naming follows it to "contracts_history") and adds "notes". Applied on top of whatever
        // B actually left in the database.
        await using var c = new PolicyContext(cs, PolicyShape.C, snapshotModel: bModel);
        var cModel = c.GetService<IDesignTimeModel>().Model;
        await ApplyAsync(c, cs, previousModel: bModel);

        await AssertSchemaAsync(
            cs,
            mainTable: "contracts",
            mainColumns: ["id", "number", "premium", "notes"],
            historyTable: "contracts_history",
            historyColumns: ["id", "number", "premium", "notes", "legacy_field"]);

        Assert.False(await TableExistsAsync(cs, "policies"));
        Assert.False(await TableExistsAsync(cs, "policies_history"));

        // Every version written across the whole chain is still there, readable through the new name,
        // including the one written before the table was ever renamed.
        var totalHistoryRows = await ScalarAsync(cs, "select count(*) from contracts_history");
        Assert.Equal("2", totalHistoryRows);

        var firstRowLegacy = await ScalarAsync(cs, "select legacy_field from contracts_history where id = 1");
        Assert.Equal("keep-me", firstRowLegacy);

        // Fully writable under the final shape.
        c.Policies.Add(new Policy { Id = 3, Number = "P-3", Premium = 50m, Notes = "new" });
        await c.SaveChangesAsync(Ct);
        Assert.Equal("3", await ScalarAsync(cs, "select count(*) from contracts_history"));
    }

    private static async Task ApplyAsync(DbContext context, string connectionString, IModel? previousModel)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            previousModel?.GetRelationalModel(), model.GetRelationalModel());
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, model);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = command.CommandText;
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    private static async Task AssertSchemaAsync(
        string connectionString,
        string mainTable,
        string[] mainColumns,
        string historyTable,
        string[] historyColumns)
    {
        Assert.Equal(mainColumns.OrderBy(c => c, StringComparer.Ordinal), await ColumnsAsync(connectionString, mainTable));
        Assert.Equal(
            historyColumns.OrderBy(c => c, StringComparer.Ordinal),
            (await ColumnsAsync(connectionString, historyTable))
                .Except(["valid_from", "valid_to", "operation", "changed_by", "changed_by_name", "correlation_id", "reason", "extra", "history_id"]));
    }

    private static async Task<IReadOnlyList<string>> ColumnsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select column_name from information_schema.columns where table_schema = 'public' and table_name = @t order by column_name";
        command.Parameters.AddWithValue("t", table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists (
                select 1 from information_schema.tables
                where table_schema = 'public' and table_name = @t
            )
            """;
        command.Parameters.AddWithValue("t", table);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? null : value.ToString();
    }

    private enum PolicyShape
    {
        A,
        B,
        C,
    }

    // One CLR type spans all three migrations, deliberately: DESIGN.md D15's rename mechanism depends on
    // the SAME entity type persisting across snapshots (only its ToTable(...) name changes) — swapping in
    // a differently-named CLR class at C would make the differ see an unrelated entity appearing instead
    // of a rename, which is a different scenario (covered by EntityRemovalAndReadditionTests), not this
    // one. PolicyContext maps whichever columns each stage needs and ignores the rest, the same way a
    // real project's entity class changes field-by-field across successive commits.
    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public string? LegacyField { get; set; }
        public decimal Premium { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class PolicyContext(string connectionString, PolicyShape shape, IModel? snapshotModel = null)
        : DbContext
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString).UseHindsight().EnableServiceProviderCaching(false);
            if (snapshotModel is not null)
            {
                options.ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>();
                StubMigrationsAssembly.Snapshot.Value = new StubModelSnapshot(snapshotModel);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable(shape == PolicyShape.C ? "contracts" : "policies");
            policy.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(p => p.Number).HasColumnName("number");

            if (shape == PolicyShape.A)
            {
                policy.Property(p => p.LegacyField).HasColumnName("legacy_field");
                policy.Ignore(p => p.Premium);
                policy.Ignore(p => p.Notes);
            }
            else
            {
                policy.Ignore(p => p.LegacyField);
                policy.Property(p => p.Premium).HasColumnName("premium");

                if (shape == PolicyShape.C)
                {
                    policy.Property(p => p.Notes).HasColumnName("notes");
                }
                else
                {
                    policy.Ignore(p => p.Notes);
                }
            }

            policy.IsTemporal();
        }
    }

    private sealed class StubMigrationsAssembly : IMigrationsAssembly
    {
        public static readonly AsyncLocal<StubModelSnapshot?> Snapshot = new();

        public IReadOnlyDictionary<string, TypeInfo> Migrations { get; } = new Dictionary<string, TypeInfo>();

        public ModelSnapshot? ModelSnapshot => Snapshot.Value;

        public Assembly Assembly => typeof(StubMigrationsAssembly).Assembly;

        public string? FindMigrationId(string nameOrId) => null;

        public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
            => throw new NotSupportedException();
    }

    private sealed class StubModelSnapshot(IModel model) : ModelSnapshot
    {
        public override IModel Model { get; } = model;

        protected override void BuildModel(ModelBuilder modelBuilder)
        {
        }
    }
}
