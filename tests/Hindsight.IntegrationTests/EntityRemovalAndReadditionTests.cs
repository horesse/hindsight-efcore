using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D6: "detection doesn't care *why* the entity type left <c>temporalEntityTypes</c>
/// (<c>IsTemporal()</c> removed vs. the CLR type/DbSet removed vs. <c>Ignore()</c>d), only that it's no
/// longer being rebuilt". <see cref="HistoryTableRetentionOnDetemporalizeTests"/> covers the
/// <c>IsTemporal()</c>-removed case, where the CLR type stays mapped (just non-temporal); this file
/// covers the CLR type disappearing from the model entirely — a stronger form of the same mechanism —
/// and the case neither of those files covers at all: re-adding the entity as temporal again later, which
/// exercises D6's orphaning and D15's identity-reuse together (the re-added entity must reconnect to the
/// SAME history table rather than spawn an unrelated one under a fresh identity).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EntityRemovalAndReadditionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_the_entity_type_entirely_keeps_the_history_table_and_its_data()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Removing_the_entity_type_entirely_keeps_the_history_table_and_its_data), Ct);

        // v1: Policy is temporal, mapped as usual.
        await using var v1 = new TemporalContext(cs);
        await v1.Database.EnsureCreatedAsync(Ct);
        v1.Policies.Add(new Policy { Id = 1, Number = "P-1" });
        await v1.SaveChangesAsync(Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: Policy is not part of the model at all — no Entity<Policy>() call, no DbSet, nothing. Not
        // merely "IsTemporal() removed" (HistoryTableRetentionOnDetemporalizeTests already covers that
        // weaker case) — the CLR type itself is gone.
        await using var v2 = new NoEntityContext(cs, v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        // The main table is genuinely gone (nothing preserves it — only history is protected); the
        // history table must not be.
        Assert.Contains(operations.OfType<DropTableOperation>(), op => op.Name == "policies");
        Assert.DoesNotContain(operations.OfType<DropTableOperation>(), op => op.Name == "policies_history");

        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);
        foreach (var command in commands)
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        Assert.True(await TableExistsAsync(cs, "policies_history"));
        Assert.False(await TableExistsAsync(cs, "policies"));
        Assert.Equal(1, await HistoryRowCountAsync(cs));
    }

    [Fact]
    public async Task Re_adding_the_entity_type_as_temporal_reconnects_to_the_same_history_table()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Re_adding_the_entity_type_as_temporal_reconnects_to_the_same_history_table), Ct);

        // v1: Policy is temporal; seed one row.
        await using var v1 = new TemporalContext(cs);
        await v1.Database.EnsureCreatedAsync(Ct);
        v1.Policies.Add(new Policy { Id = 1, Number = "P-1" });
        await v1.SaveChangesAsync(Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: removed entirely (same transition as the test above) — applied for real so v3 starts from
        // an actual, already-migrated database, not merely a diffed-but-unapplied model.
        await using var v2 = new NoEntityContext(cs, v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;
        var v1ToV2 = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());
        foreach (var command in v2.GetService<IMigrationsSqlGenerator>().Generate(v1ToV2, v2Model))
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        // v3: Policy comes back, temporal again.
        await using var v3 = new TemporalContext(cs, v2Model);
        var v3Model = v3.GetService<IDesignTimeModel>().Model;
        var v2ToV3 = v3.GetService<IMigrationsModelDiffer>().GetDifferences(
            v2Model.GetRelationalModel(), v3Model.GetRelationalModel());

        // The re-added entity must reuse the SAME history identity (DESIGN.md D15) rather than spawn an
        // unrelated one — so the only table operation here is a fresh CreateTable for "policies" (which
        // really was dropped in v2); "policies_history" must not be created or dropped again.
        Assert.Contains(v2ToV3.OfType<CreateTableOperation>(), op => op.Name == "policies");
        Assert.DoesNotContain(v2ToV3.OfType<CreateTableOperation>(), op => op.Name == "policies_history");
        Assert.DoesNotContain(v2ToV3.OfType<DropTableOperation>(), op => op.Name == "policies_history");

        foreach (var command in v3.GetService<IMigrationsSqlGenerator>().Generate(v2ToV3, v3Model))
        {
            await v3.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        // The row from before the removal is still there under the reconnected history table.
        Assert.Equal(1, await HistoryRowCountAsync(cs));

        // And the reconnected entity is genuinely writable again.
        v3.Policies.Add(new Policy { Id = 2, Number = "P-2" });
        await v3.SaveChangesAsync(Ct);

        Assert.Equal(2, await HistoryRowCountAsync(cs));
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

    private static async Task<long> HistoryRowCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from policies_history";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private abstract class EvolvingContext(string connectionString, IModel? snapshotModel) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString).UseHindsight().EnableServiceProviderCaching(false);
            if (snapshotModel is not null)
            {
                options.ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>();
                StubMigrationsAssembly.Snapshot.Value = new StubModelSnapshot(snapshotModel);
            }
        }
    }

    private sealed class TemporalContext(string connectionString, IModel? snapshotModel = null)
        : EvolvingContext(connectionString, snapshotModel)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(p => p.Number).HasColumnName("number");
            policy.IsTemporal();
        }
    }

    // No DbSet<Policy> property and no OnModelCreating override — Policy genuinely is not part of this
    // model, unlike merely calling Ignore(...) or dropping IsTemporal() on an entity that stays mapped.
    private sealed class NoEntityContext(string connectionString, IModel? snapshotModel = null)
        : EvolvingContext(connectionString, snapshotModel);

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
