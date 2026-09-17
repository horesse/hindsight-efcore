using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D6, "Existing non-empty table made temporal": the migration that creates a new history
/// table also seeds an initial version for every row already in the main table, with one unconditional
/// <c>INSERT ... SELECT</c> emitted by <c>HindsightMigrationsSqlGenerator</c> right after the history
/// table's <c>CreateTableOperation</c> (and, in Trigger mode, after the trigger). This automates the
/// manual workaround `docs/migrations/existing-tables.md` used to document. Exercised in both writer
/// modes, since the seeding statement itself is plain migration DDL — neither writer executes it — and
/// against the brand-new-entity case, where the main table is created empty in the same migration and the
/// <c>SELECT</c> is a harmless no-op; the generator makes no attempt to tell the two cases apart.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistorySeedingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Adding_IsTemporal_to_an_existing_non_empty_table_seeds_one_version_per_row(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            TrimmedDbName(nameof(Adding_IsTemporal_to_an_existing_non_empty_table_seeds_one_version_per_row), writer), Ct);

        // v1: Policy exists and already has rows, but is not temporal at all — no history table, no
        // Hindsight annotations on the entity. This is the "existing table" half of D6's bullet.
        await using var v1 = new PolicyContext(cs, temporal: false, writer);
        await v1.Database.EnsureCreatedAsync(Ct);

        v1.Policies.AddRange(
            new Policy { Id = 1, Number = "P-1", Premium = 100m },
            new Policy { Id = 2, Number = "P-2", Premium = 200m });
        await v1.SaveChangesAsync(Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        var beforeMigration = DateTime.UtcNow;

        // v2: the same entity, now temporal, built from v1's snapshot — the differ sees a genuine
        // CreateTableOperation for the history table (unlike every other test in this project, which
        // starts from an entity that was already temporal in its very first migration).
        await using var v2 = new PolicyContext(cs, temporal: true, writer, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        foreach (var command in v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model))
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        var afterMigration = DateTime.UtcNow;

        var rows = await ReadHistoryAsync(cs);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal((short)1, row.Operation); // insert
            Assert.Equal(DateTime.MaxValue, row.ValidTo); // 'infinity'
            Assert.InRange(row.ValidFrom, beforeMigration.AddSeconds(-2), afterMigration.AddSeconds(2));
            Assert.Null(row.ChangedBy);
            Assert.Null(row.ChangedByName);
            Assert.Null(row.CorrelationId);
            Assert.Null(row.Reason);
            Assert.Null(row.Extra);
        });
        Assert.Contains(rows, r => r.Id == 1 && r.Number == "P-1" && r.Premium == 100m);
        Assert.Contains(rows, r => r.Id == 2 && r.Number == "P-2" && r.Premium == 200m);

        // Fully writable afterwards, under whichever writer mode is now configured.
        v2.Policies.Add(new Policy { Id = 3, Number = "P-3", Premium = 300m });
        await v2.SaveChangesAsync(Ct);
        Assert.Equal("3", await ScalarAsync(cs, "select count(*) from policies_history"));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Brand_new_temporal_entity_seeds_zero_rows(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            TrimmedDbName(nameof(Brand_new_temporal_entity_seeds_zero_rows), writer), Ct);

        // No prior model: the entity is temporal from its very first migration, so the seeding statement
        // (unconditional, DESIGN.md D6) runs against a main table created empty in that same migration —
        // the SELECT naturally returns zero rows, and this must not error either way.
        await using var db = new PolicyContext(cs, temporal: true, writer);
        await db.Database.EnsureCreatedAsync(Ct);

        Assert.Equal("0", await ScalarAsync(cs, "select count(*) from policies_history"));

        db.Policies.Add(new Policy { Id = 1, Number = "P-1", Premium = 50m });
        await db.SaveChangesAsync(Ct);
        Assert.Equal("1", await ScalarAsync(cs, "select count(*) from policies_history"));
    }

    // PostgreSQL truncates identifiers at 63 bytes; keep room for a per-writer suffix so the two [Theory]
    // runs of the same test method do not collide on the database name.
    private static string TrimmedDbName(string dbName, HistoryWriter writer)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = dbName.Length > 58 ? dbName[..58] : dbName;
        return trimmed + suffix;
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

    private static async Task<List<HistoryRow>> ReadHistoryAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select id, number, premium, valid_from, valid_to, operation,
                   changed_by, changed_by_name, correlation_id, reason, extra
            from policies_history
            order by id
            """;

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetFieldValue<DateTime>(3),
                reader.GetFieldValue<DateTime>(4),
                reader.GetInt16(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return rows;
    }

    private sealed record HistoryRow(
        int Id,
        string Number,
        decimal Premium,
        DateTime ValidFrom,
        DateTime ValidTo,
        short Operation,
        string? ChangedBy,
        string? ChangedByName,
        string? CorrelationId,
        string? Reason,
        string? Extra);

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public decimal Premium { get; set; }
    }

    private sealed class PolicyContext(string connectionString, bool temporal, HistoryWriter writer, IModel? snapshotModel = null)
        : DbContext
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString)
                .UseHindsight(h => h.UseHistoryWriter(writer))
                .EnableServiceProviderCaching(false);
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
            policy.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(p => p.Number).HasColumnName("number");
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);

            if (temporal)
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
