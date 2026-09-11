using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D6 / golden rule 3, extended to whole entity types: removing <c>IsTemporal()</c> from an
/// entity entirely (as opposed to removing one property, which D6 already covered) must not drop its
/// history table. <c>HistoryEntityTypeConvention</c> selects the entity types to build history for by
/// filtering the CURRENT model for <see cref="HindsightAnnotationNames.IsTemporal"/>, so an entity that
/// stops being temporal falls out of that set; without the whole-entity-type orphaning this test
/// guards, the differ would see no reason to keep the now-unreferenced history table and would emit a
/// <c>DropTableOperation</c> for it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryTableRetentionOnDetemporalizeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_IsTemporal_keeps_the_history_table_and_its_data()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Removing_IsTemporal_keeps_the_history_table_and_its_data), Ct);

        // v1: Policy is temporal; create the schema for real and seed one history row.
        await using var v1 = new DetemporalizeContext(cs, isTemporal: true);
        await v1.Database.EnsureCreatedAsync(Ct);
        await v1.Database.ExecuteSqlRawAsync(
            """
            insert into policies (id, number) values (1, 'P-1');
            insert into policies_history (id, number, valid_from, valid_to, operation)
            values (1, 'P-1', now(), 'infinity', 1);
            """,
            Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: IsTemporal() removed entirely. The previous model is fed to the convention as the "snapshot".
        await using var v2 = new DetemporalizeContext(cs, isTemporal: false, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        // Without whole-entity-type orphaning this would contain a DropTableOperation for
        // "policies_history".
        Assert.DoesNotContain(
            operations.OfType<DropTableOperation>(), op => op.Name == "policies_history");

        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);
        foreach (var command in commands)
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        Assert.True(await TableExistsAsync(cs, "policies_history"));
        Assert.Equal(1, await HistoryRowCountAsync(cs));
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

    private sealed class DetemporalizeContext(string connectionString, bool isTemporal, IModel? snapshotModel = null)
        : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString).UseHindsight();
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
