using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D15: a <see cref="RenameTableOperation"/> also covers a pure schema move (same table name,
/// only <c>NewSchema</c> set) — a case the original table-name-rename fix (<see cref="TableRenameTriggerDdlTests"/>)
/// never exercised. PostgreSQL's <c>ALTER TABLE ... SET SCHEMA</c> moves every object the table owns
/// (its indexes included) into the new schema automatically; a trigger function is its own standalone
/// object and is not moved by it. Both writer modes must keep writing history without a gap once the
/// move has happened.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaRenameTriggerDdlTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Trigger)]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Moving_the_main_table_schema_keeps_history_writable(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            $"{nameof(Moving_the_main_table_schema_keeps_history_writable)}_{writer}", Ct);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync(Ct);
            await ExecAsync(conn, "create schema app1; create schema app2;");
        }

        await using var v1 = Build(cs, "app1", writer);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;
        await ApplySchemaAsync(v1, cs);

        // Through SaveChanges, not raw SQL: raw SQL bypasses Interceptor mode entirely (D4), which would
        // prove nothing about it in that half of this theory.
        await using (var seed = Build(cs, "app1", writer))
        {
            seed.Policies.Add(new Policy { Id = 1, Number = "P-1" });
            await seed.SaveChangesAsync(Ct);
        }

        await using var v2 = Build(cs, "app2", writer, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        Assert.Contains(operations, op =>
            op is RenameTableOperation r && r.Schema == "app1" && r.Name == "policies"
            && r.NewSchema == "app2" && r.NewName == "policies");
        Assert.Contains(operations, op =>
            op is RenameTableOperation r && r.Schema == "app1" && r.Name == "policies_history"
            && r.NewSchema == "app2" && r.NewName == "policies_history");
        Assert.DoesNotContain(operations, op => op is CreateTableOperation or DropTableOperation);

        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);

        await using var apply = new NpgsqlConnection(cs);
        await apply.OpenAsync(Ct);
        foreach (var command in commands)
        {
            await ExecAsync(apply, command.CommandText);
        }

        // The pre-move row and table must still be reachable, now under the new schema, with no gap.
        var preMoveHistoryCount = await ScalarAsync(apply, "select count(*) from app2.policies_history where id = 1");
        Assert.Equal(1L, preMoveHistoryCount);

        var oldSchemaHasTable = await ScalarAsync(
            apply, "select count(*) from information_schema.tables where table_schema = 'app1' and table_name = 'policies'");
        Assert.Equal(0L, oldSchemaHasTable);

        // A write after the move must still produce history — this is what the fix exists for. Through
        // SaveChanges so this exercises both writer modes identically (raw SQL bypasses Interceptor mode
        // entirely per D4, which would prove nothing about it here).
        await using (var writeContext = Build(cs, "app2", writer))
        {
            var tracked = await writeContext.Policies.SingleAsync(p => p.Id == 1, Ct);
            tracked.Number = "P-1a";
            writeContext.Policies.Add(new Policy { Id = 2, Number = "P-2" });
            await writeContext.SaveChangesAsync(Ct);

            writeContext.Policies.Remove(writeContext.Policies.Local.Single(p => p.Id == 2));
            await writeContext.SaveChangesAsync(Ct);
        }

        var rowsForId1 = await ScalarAsync(apply, "select count(*) from app2.policies_history where id = 1");
        Assert.Equal(2L, rowsForId1); // pre-move insert + post-move update

        var rowsForId2 = await ScalarAsync(apply, "select count(*) from app2.policies_history where id = 2");
        Assert.Equal(2L, rowsForId2); // post-move insert + delete

        // The period-range index moved with the table (automatic in PostgreSQL) and kept its name; a
        // stray ALTER INDEX targeting the old schema, or one renaming it to its own name, would already
        // have failed above with a PostgresException before reaching this point.
        var indexInNewSchema = await ScalarAsync(
            apply,
            """
            select count(*) from pg_indexes
            where schemaname = 'app2' and tablename = 'policies_history' and indexname = 'ix_policies_history_period'
            """);
        Assert.Equal(1L, indexInNewSchema);
    }

    private static async Task ApplySchemaAsync(SchemaMoveContext db, string connectionString)
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

    private static SchemaMoveContext Build(
        string connectionString, string schema, HistoryWriter writer, IModel? snapshotModel = null)
    {
        var builder = new DbContextOptionsBuilder<SchemaMoveContext>()
            .UseNpgsql(connectionString)
            .EnableServiceProviderCaching(false)
            .ReplaceService<IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .UseHindsight(h => h.UseHistoryWriter(writer));

        return new SchemaMoveContext(builder.Options, schema, snapshotModel);
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class SchemaAwareModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            var ctx = (SchemaMoveContext)context;
            return (context.GetType(), ctx.Schema, designTime);
        }
    }

    private sealed class SchemaMoveContext(
        DbContextOptions<SchemaMoveContext> options, string schema, IModel? snapshotModel = null)
        : DbContext(options)
    {
        public string Schema => schema;

        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>();
            StubMigrationsAssembly.Snapshot.Value = snapshotModel is null ? null : new StubModelSnapshot(snapshotModel);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies", schema);
            policy.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(p => p.Number).HasColumnName("number");
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
