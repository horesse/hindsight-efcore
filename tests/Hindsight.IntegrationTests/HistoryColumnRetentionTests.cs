using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D6 / golden rule 3 on a real database: removing a property from a temporal entity must
/// drop the column from the main table only. The migrations differ produces no destructive operation
/// on the history table, and after the generated DDL is applied the history column is still there,
/// nullable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryColumnRetentionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_a_property_drops_the_main_column_and_keeps_the_history_column_nullable()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Removing_a_property_drops_the_main_column_and_keeps_the_history_column_nullable), Ct);

        // v1: the entity still has LegacyNote; create the schema for real.
        await using var v1 = new RetentionContext(cs, withLegacyNote: true);
        await v1.Database.EnsureCreatedAsync(Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: LegacyNote removed. The previous model is fed to the convention as the "snapshot".
        await using var v2 = new RetentionContext(cs, withLegacyNote: false, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        Assert.Contains(
            operations.OfType<DropColumnOperation>(),
            op => op.Table == "policies" && op.Name == "legacy_note");
        Assert.DoesNotContain(
            operations.OfType<DropColumnOperation>(), op => op.Table == "policies_history");
        Assert.DoesNotContain(
            operations.OfType<DropTableOperation>(), op => op.Name == "policies_history");
        Assert.DoesNotContain(
            operations.OfType<AlterColumnOperation>(), op => op.Table == "policies_history");

        var commands = v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model);
        foreach (var command in commands)
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        Assert.Null(await ColumnNullabilityAsync(cs, "policies", "legacy_note"));
        Assert.Equal("YES", await ColumnNullabilityAsync(cs, "policies_history", "legacy_note"));
    }

    private static async Task<string?> ColumnNullabilityAsync(string connectionString, string table, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select is_nullable from information_schema.columns
            where table_schema = 'public' and table_name = @t and column_name = @c
            """;
        command.Parameters.AddWithValue("t", table);
        command.Parameters.AddWithValue("c", column);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public string LegacyNote { get; set; } = "";
    }

    private sealed class RetentionContext(string connectionString, bool withLegacyNote, IModel? snapshotModel = null)
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
            if (withLegacyNote)
            {
                policy.Property(p => p.LegacyNote).HasColumnName("legacy_note");
            }
            else
            {
                policy.Ignore(p => p.LegacyNote);
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
