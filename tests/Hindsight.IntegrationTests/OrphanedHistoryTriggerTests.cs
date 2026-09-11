using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// In <see cref="HistoryWriter.Trigger"/> mode, the physical trigger lives on the main table, not the
/// history table (DESIGN.md D3). <see cref="HistoryTableRetentionOnDetemporalizeTests"/> covers keeping
/// the history table itself when <c>IsTemporal()</c> is removed entirely; this covers the trigger that
/// keeps firing on the main table afterwards. Nothing in the model diff between the temporal and
/// de-temporalized versions touches the main table (that is the whole point of orphaning the history
/// entity type unchanged), so <c>CollectTriggerModels</c> never builds a trigger model for it and no
/// <c>DROP FUNCTION ... CASCADE</c> is ever emitted — the trigger silently keeps writing into the now
/// orphaned history table.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrphanedHistoryTriggerTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_IsTemporal_stops_the_trigger_from_writing_history()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Removing_IsTemporal_stops_the_trigger_from_writing_history), Ct);

        // v1: Policy is temporal, Trigger mode. Apply the real DDL so the function and trigger actually
        // exist on 'policies'.
        await using var v1 = new DetemporalizeTriggerContext(cs, isTemporal: true);
        await ApplySchemaAsync(v1);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: IsTemporal() removed entirely. The previous model is fed to the convention as the
        // "snapshot", exactly like a real `dotnet ef migrations add` would. Migrate for real again.
        await using var v2 = new DetemporalizeTriggerContext(cs, isTemporal: false, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());
        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);
        foreach (var command in commands)
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        // Raw SQL only, bypassing EF entirely: the entity is no longer temporal in the model, so nothing
        // should write history for it any more, no matter what still fires at the database level.
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "insert into policies (id, number) values (1, 'P-1')";
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await using var count = conn.CreateCommand();
        count.CommandText = "select count(*) from policies_history";
        var historyRows = (long)(await count.ExecuteScalarAsync(Ct))!;

        Assert.Equal(0, historyRows);
    }

    private static async Task ApplySchemaAsync(DetemporalizeTriggerContext db)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(operations, model);
        foreach (var command in commands)
        {
            await db.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class DetemporalizeTriggerContext(string connectionString, bool isTemporal, IModel? snapshotModel = null)
        : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString).UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger));
            if (snapshotModel is not null)
            {
                options.ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>();
                StubMigrationsAssembly.Snapshot.Value = new StubModelSnapshot(snapshotModel);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");

            if (isTemporal)
            {
                policy.IsTemporal();
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
