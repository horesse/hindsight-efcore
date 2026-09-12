using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D15: renaming a temporal entity's main table (<c>ToTable</c>) or its history table
/// (<c>UseHistoryTable</c>) must produce a working trigger afterwards, with history preserved and
/// continued under the new name rather than split or lost. Before D15 the history entity's identity in
/// the model was its own table name, so any such rename was invisible to the differ as a rename at
/// all — it showed up as an unrelated table appearing (the old one kept alive, but orphaned) rather
/// than a genuine <see cref="RenameTableOperation"/>. These are the end-to-end regressions that would
/// have caught it, run against a real trigger-function/trigger pair on PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TableRenameTriggerDdlTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Renaming_the_main_table_produces_one_rename_for_it_and_one_for_its_default_named_history_table()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Renaming_the_main_table_produces_one_rename_for_it_and_one_for_its_default_named_history_table), Ct);

        await using var v1 = Build(mainTable: "policies", historyTable: null);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;
        await ApplySchemaAsync(v1, cs);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await ExecAsync(conn, "insert into policies (id, number) values (1, 'P-1')");
            await ExecAsync(conn, "update policies set number = 'P-1a' where id = 1");
        }

        await using var v2 = Build(mainTable: "policies2", historyTable: null, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        // The crux this test exists to lock in (DESIGN.md D15): the default-suffix history table name
        // is derived from the main table's own name, so renaming the main table renames both — as two
        // genuine RenameTableOperations, not a DropTable/CreateTable pair for the history side.
        Assert.Contains(operations, op =>
            op is RenameTableOperation r && r.Name == "policies" && r.NewName == "policies2");
        Assert.Contains(operations, op =>
            op is RenameTableOperation r && r.Name == "policies_history" && r.NewName == "policies2_history");
        Assert.DoesNotContain(operations, op => op is CreateTableOperation or DropTableOperation);

        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);

        await using var apply = new NpgsqlConnection(cs);
        await apply.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await ExecAsync(apply, command.CommandText);
        }

        // Raw SQL against the renamed main table: the trigger must still fire, writing into the
        // (also renamed) history table, continuing the same version chain rather than starting a new one.
        await ExecAsync(apply, "insert into policies2 (id, number) values (2, 'P-2')");
        await ExecAsync(apply, "update policies2 set number = 'P-2a' where id = 2");
        await ExecAsync(apply, "delete from policies2 where id = 2");

        var historyTable = HistoryTableName(v2Model, typeof(Policy));
        Assert.Equal("policies2_history", historyTable);

        var rowsForId1 = await ReadHistoryAsync(apply, historyTable, id: 1);
        Assert.Equal(2, rowsForId1.Count); // pre-rename insert + update, still there under the new table
        Assert.Equal([1, 2], rowsForId1.Select(r => r.Operation));

        var rowsForId2 = await ReadHistoryAsync(apply, historyTable, id: 2);
        Assert.Equal([1, 2, 3], rowsForId2.Select(r => r.Operation));
        for (var i = 1; i < rowsForId2.Count; i++)
        {
            Assert.True(rowsForId2[i - 1].ValidTo <= rowsForId2[i].ValidFrom);
        }

        var oldTableStillExists = await ScalarAsync(apply,
            "select count(*) from information_schema.tables where table_name = 'policies_history'");
        Assert.Equal(0L, oldTableStillExists);
    }

    [Fact]
    public async Task Renaming_only_the_history_table_keeps_it_writable_and_keeps_its_data()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Renaming_only_the_history_table_keeps_it_writable_and_keeps_its_data), Ct);

        await using var v1 = Build(mainTable: "policies", historyTable: "policy_history_v1");
        var v1Model = v1.GetService<IDesignTimeModel>().Model;
        await ApplySchemaAsync(v1, cs);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await ExecAsync(conn, "insert into policies (id, number) values (1, 'P-1')");
        }

        await using var v2 = Build(mainTable: "policies", historyTable: "policy_history_v2", snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        // The main table is untouched: only the history table's own rename should appear.
        Assert.DoesNotContain(operations, op => op is RenameTableOperation r && r.Name == "policies");
        Assert.Contains(operations, op =>
            op is RenameTableOperation r && r.Name == "policy_history_v1" && r.NewName == "policy_history_v2");
        Assert.DoesNotContain(operations, op => op is CreateTableOperation or DropTableOperation);

        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);

        await using var apply = new NpgsqlConnection(cs);
        await apply.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await ExecAsync(apply, command.CommandText);
        }

        await ExecAsync(apply, "update policies set number = 'P-1a' where id = 1");
        await ExecAsync(apply, "delete from policies where id = 1");

        var rows = await ReadHistoryAsync(apply, "policy_history_v2", id: 1);
        Assert.Equal([1, 2, 3], rows.Select(r => r.Operation)); // insert (pre-rename) + update + delete

        var oldTableStillExists = await ScalarAsync(apply,
            "select count(*) from information_schema.tables where table_name = 'policy_history_v1'");
        Assert.Equal(0L, oldTableStillExists);
    }

    [Fact]
    public async Task Renaming_the_main_table_with_a_fixed_history_table_name_needs_no_trigger_ddl()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Renaming_the_main_table_with_a_fixed_history_table_name_needs_no_trigger_ddl), Ct);

        await using var v1 = Build(mainTable: "policies", historyTable: "policies_history_fixed");
        var v1Model = v1.GetService<IDesignTimeModel>().Model;
        await ApplySchemaAsync(v1, cs);

        await using var v2 = Build(mainTable: "policies2", historyTable: "policies_history_fixed", snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());
        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);
        var commandTexts = commands.Select(c => c.CommandText).ToList();

        // Nothing about the trigger function/trigger needs regenerating: PostgreSQL tracks the trigger
        // by the main table's OID (it follows a plain ALTER TABLE ... RENAME automatically), and the
        // function body only ever names the (unchanged) history table.
        Assert.DoesNotContain(commandTexts, text => text.Contains("FUNCTION", StringComparison.Ordinal));
        Assert.DoesNotContain(commandTexts, text => text.Contains("TRIGGER", StringComparison.Ordinal));

        await using var apply = new NpgsqlConnection(cs);
        await apply.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await ExecAsync(apply, command.CommandText);
        }

        await ExecAsync(apply, "insert into policies2 (id, number) values (1, 'P-1')");
        await ExecAsync(apply, "delete from policies2 where id = 1");

        var rows = await ReadHistoryAsync(apply, "policies_history_fixed", id: 1);
        Assert.Equal([1, 3], rows.Select(r => r.Operation));
    }

    private static string HistoryTableName(IModel model, Type sourceClrType)
    {
        var source = model.FindEntityType(sourceClrType)!;
        var identity = (string)source[HindsightAnnotationNames.HistoryEntityType]!;
        return model.FindEntityType(identity)!.GetTableName()!;
    }

    private static async Task ApplySchemaAsync(RenameContext db, string connectionString)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations, model);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await ExecAsync(conn, command.CommandText);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<HistoryRow>> ReadHistoryAsync(NpgsqlConnection conn, string historyTable, int id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
            select valid_from, valid_to, operation
            from "{historyTable}" where id = @id
            order by valid_from, history_id
            """;
        cmd.Parameters.AddWithValue("id", id);

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetFieldValue<DateTime>(0),
                reader.GetFieldValue<DateTime>(1),
                reader.GetInt16(2)));
        }

        return rows;
    }

    private static RenameContext Build(
        string mainTable, string? historyTable, string? connectionString = null, IModel? snapshotModel = null)
    {
        var builder = new DbContextOptionsBuilder<RenameContext>()
            .UseNpgsql(connectionString ?? "Host=localhost;Database=unused")
            .ReplaceService<IModelCacheKeyFactory, RenameAwareModelCacheKeyFactory>()
            .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger))
            // Each test builds several deliberately distinct, short-lived contexts (one per rename side)
            // purely to diff their models — exactly the "unique service provider per context" pattern
            // this EF Core diagnostic exists to catch in long-lived production code, not in a handful of
            // throwaway test contexts. Left unsuppressed, it throws once the whole test process's
            // cumulative count of such contexts (this file's plus every other test file's) crosses EF's
            // built-in threshold — a threshold this file alone doesn't control.
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        return new RenameContext(builder.Options, mainTable, historyTable, snapshotModel);
    }

    private sealed record HistoryRow(DateTime ValidFrom, DateTime ValidTo, short Operation);

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class RenameAwareModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var ctx = (RenameContext)context;
            return (context.GetType(), ctx.MainTable, ctx.HistoryTable, designTime);
        }
    }

    private sealed class RenameContext(
        DbContextOptions<RenameContext> options, string mainTable, string? historyTable, IModel? snapshotModel = null)
        : DbContext(options)
    {
        public string MainTable => mainTable;

        public string? HistoryTable => historyTable;

        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Always replaced (never conditionally), so every context in this file shares one
            // DbContextOptions shape and therefore one cached internal service provider: whether there
            // is a snapshot to feed the convention is controlled by the AsyncLocal's value below, not by
            // varying which services are replaced. A per-test-method conditional replacement here would
            // each need its own service provider, adding to the process-wide count that trips EF Core's
            // "more than twenty service providers" diagnostic once enough unrelated test files do the
            // same thing.
            optionsBuilder.ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>();
            StubMigrationsAssembly.Snapshot.Value = snapshotModel is null ? null : new StubModelSnapshot(snapshotModel);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable(mainTable);
            policy.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(p => p.Number).HasColumnName("number");

            if (historyTable is null)
            {
                policy.IsTemporal();
            }
            else
            {
                policy.IsTemporal(t => t.UseHistoryTable(historyTable));
            }
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
