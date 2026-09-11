using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// The period-range index every history table gets (DESIGN.md D5, .claude/rules/sql-and-migrations.md):
/// <c>gist (tstzrange(valid_from, valid_to))</c>, emitted by <c>HindsightMigrationsSqlGenerator</c>
/// right after the history table's <c>CreateTableOperation</c>. Run against both a single-column-key
/// entity and a composite-key entity — the index is a single column and never references key columns at
/// all, so key shape cannot affect it — and in both writer modes, since the index has nothing to do with
/// which writer is configured (unlike <c>HistoryTableCreationTests</c>' version-index test, which only
/// needs one writer mode because <c>AddVersionIndex</c> is a plain EF-model index, unaffected by DDL
/// registration).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryPeriodRangeIndexTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task History_table_has_a_gist_index_on_the_period_range_for_a_single_column_key(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync("gist_index_single_key_" + writer, Ct);
        await using var db = new PolicyContext(Build<PolicyContext>(cs, writer));
        await db.Database.EnsureCreatedAsync(Ct);

        var (accessMethod, definition) = await ReadIndexAsync(cs, "policies_history", "ix_policies_history_period");

        Assert.Equal("gist", accessMethod);
        Assert.Contains("tstzrange(valid_from, valid_to)", definition);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task History_table_has_a_gist_index_on_the_period_range_for_a_composite_key(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync("gist_index_composite_key_" + writer, Ct);
        await using var db = new ReadingContext(Build<ReadingContext>(cs, writer));
        await db.Database.EnsureCreatedAsync(Ct);

        var (accessMethod, definition) = await ReadIndexAsync(cs, "readings_history", "ix_readings_history_period");

        Assert.Equal("gist", accessMethod);
        Assert.Contains("tstzrange(valid_from, valid_to)", definition);
    }

    private static DbContextOptions<TContext> Build<TContext>(string connectionString, HistoryWriter writer)
        where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;

    private static async Task<(string AccessMethod, string Definition)> ReadIndexAsync(
        string connectionString, string table, string indexName)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);

        var accessMethod = await ScalarAsync(conn,
            $"""
            select am.amname
            from pg_index i
            join pg_class c on c.oid = i.indexrelid
            join pg_am am on am.oid = c.relam
            where i.indrelid = '{table}'::regclass and c.relname = '{indexName}'
            """);

        var definition = await ScalarAsync(conn,
            $"select indexdef from pg_indexes where tablename = '{table}' and indexname = '{indexName}'");

        return (accessMethod, definition);
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? string.Empty : value.ToString()!;
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.IsTemporal();
        }
    }

    private sealed class Reading
    {
        public int SensorId { get; set; }
        public int Sequence { get; set; }
        public double Value { get; set; }
    }

    private sealed class ReadingContext(DbContextOptions<ReadingContext> options) : DbContext(options)
    {
        public DbSet<Reading> Readings => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var reading = modelBuilder.Entity<Reading>();
            reading.ToTable("readings");
            reading.HasKey(r => new { r.SensorId, r.Sequence });
            reading.Property(r => r.SensorId).HasColumnName("sensor_id");
            reading.Property(r => r.Sequence).HasColumnName("sequence");
            reading.Property(r => r.Value).HasColumnName("value");
            reading.IsTemporal();
        }
    }
}
