using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// The schema-evolution mechanics DESIGN.md D6 describes (remove a property, keep it as a nullable
/// history orphan) and D5's version index are exercised elsewhere only against single-column-key
/// entities. This file re-runs the "remove a property" scenario against a composite-key entity end to
/// end on real PostgreSQL, since the version index (DESIGN.md D5: "(pk columns, valid_from desc)") is the
/// one piece of DDL whose shape actually depends on how many key columns there are.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CompositeKeySchemaEvolutionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Removing_a_property_from_a_composite_key_entity_keeps_both_key_columns_in_the_version_index()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(Removing_a_property_from_a_composite_key_entity_keeps_both_key_columns_in_the_version_index), Ct);

        // v1: Reading has a composite key (sensor_id, sequence) plus a "note" property.
        await using var v1 = new ReadingContext(cs, withNote: true);
        await v1.Database.EnsureCreatedAsync(Ct);
        v1.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10.5, Note = "first" });
        await v1.SaveChangesAsync(Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: "note" removed from the entity.
        await using var v2 = new ReadingContext(cs, withNote: false, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        Assert.Contains(
            operations.OfType<DropColumnOperation>(), op => op.Table == "readings" && op.Name == "note");
        Assert.DoesNotContain(
            operations.OfType<DropColumnOperation>(), op => op.Table == "readings_history");
        Assert.DoesNotContain(operations, op => op is DropTableOperation or AlterColumnOperation);

        foreach (var command in v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model))
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        // The main table lost the column; the history table kept it, nullable, with the old value intact.
        var mainHasNote = await ColumnExistsAsync(cs, "readings", "note");
        Assert.False(mainHasNote);

        var (historyHasNote, noteNullable) = await ColumnNullabilityAsync(cs, "readings_history", "note");
        Assert.True(historyHasNote);
        Assert.Equal("YES", noteNullable);

        var storedNote = await ScalarAsync(cs, "select note from readings_history where sensor_id = 1 and sequence = 1");
        Assert.Equal("first", storedNote);

        // The version index still covers both key columns plus valid_from — DESIGN.md D5's "(pk columns,
        // valid_from desc)" shape does not collapse to a single column just because one non-key property
        // left the entity.
        var versionIndexDef = await ScalarAsync(
            cs,
            """
            select indexdef from pg_indexes
            where tablename = 'readings_history' and indexname = 'ix_readings_history_version'
            """);
        Assert.Contains("(sensor_id, sequence, valid_from DESC)", versionIndexDef);

        // The period-range index (writer-mode-independent, DESIGN.md D14) is untouched by the column
        // removal — it was never keyed on entity columns in the first place.
        var periodIndexAccessMethod = await ScalarAsync(
            cs,
            """
            select am.amname
            from pg_index i
            join pg_class c on c.oid = i.indexrelid
            join pg_am am on am.oid = c.relam
            where i.indrelid = 'readings_history'::regclass and c.relname = 'ix_readings_history_period'
            """);
        Assert.Equal("gist", periodIndexAccessMethod);

        // Still fully writable after the migration, still keyed correctly.
        v2.Readings.Add(new Reading { SensorId = 1, Sequence = 2, Value = 11.0 });
        await v2.SaveChangesAsync(Ct);

        var historyRowCount = await ScalarAsync(cs, "select count(*) from readings_history");
        Assert.Equal("2", historyRowCount);
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string table, string column)
    {
        var (exists, _) = await ColumnNullabilityAsync(connectionString, table, column);
        return exists;
    }

    private static async Task<(bool Exists, string? Nullable)> ColumnNullabilityAsync(
        string connectionString, string table, string column)
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
        var result = await command.ExecuteScalarAsync(Ct);
        return result is null or DBNull ? (false, null) : (true, (string)result);
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

    private sealed class Reading
    {
        public int SensorId { get; set; }
        public int Sequence { get; set; }
        public double Value { get; set; }
        public string? Note { get; set; }
    }

    private sealed class ReadingContext(string connectionString, bool withNote, IModel? snapshotModel = null)
        : DbContext
    {
        public DbSet<Reading> Readings => Set<Reading>();

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
            var reading = modelBuilder.Entity<Reading>();
            reading.ToTable("readings");
            reading.HasKey(r => new { r.SensorId, r.Sequence });
            reading.Property(r => r.SensorId).HasColumnName("sensor_id").ValueGeneratedNever();
            reading.Property(r => r.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            reading.Property(r => r.Value).HasColumnName("value");
            if (withNote)
            {
                reading.Property(r => r.Note).HasColumnName("note");
            }
            else
            {
                reading.Ignore(r => r.Note);
            }

            reading.IsTemporal();
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
