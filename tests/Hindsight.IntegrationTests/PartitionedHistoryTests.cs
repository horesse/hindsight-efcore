using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// A history table range-partitioned by <c>valid_to</c> (DESIGN.md D19), converted with the exact SQL the
/// docs show (<c>docs/snippets/PartitionHistory.sql</c>, read from disk, so the page and the test cannot
/// drift apart). Proves on PostgreSQL 14 (the oldest supported) and 17 that the conversion keeps every
/// row, that both writers, both indexes and the Trigger writer's function work unchanged on the
/// partitioned table, that closing a version moves it out of the current partition, that <c>AsOf</c>
/// prunes the partitions that ended before its instant, and that retention by detaching a partition
/// records the horizon atomically.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PartitionedHistoryTests(PostgresFixture postgres17, Postgres14Fixture postgres14)
    : IClassFixture<Postgres14Fixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor, 14)]
    [InlineData(HistoryWriter.Trigger, 14)]
    [InlineData(HistoryWriter.Interceptor, 17)]
    [InlineData(HistoryWriter.Trigger, 17)]
    public async Task Converted_history_keeps_every_row_and_both_writers_keep_writing(HistoryWriter writer, int major)
    {
        await using var h = await CreateAsync(writer, major, "partition_convert");
        await h.StepAsync(db => db.Policies.Add(new Policy { Status = "Draft" }));
        await h.StepAsync(async db => (await db.Policies.SingleAsync(Ct)).Status = "Active");
        var before = await ReadRowsAsync(h.ConnectionString);

        await ConvertAsync(h.ConnectionString);

        Assert.Equal(
            before.Select(r => r with { Partition = "" }),
            (await ReadRowsAsync(h.ConnectionString)).Select(r => r with { Partition = "" }));
        Assert.True(await IsPartitionedAsync(h.ConnectionString));

        await h.StepAsync(async db => (await db.Policies.SingleAsync(Ct)).Status = "Cancelled");
        await h.StepAsync(db => db.Policies.Add(new Policy { Status = "Draft" }));
        await h.StepAsync(async db => db.Policies.Remove(await db.Policies.OrderBy(p => p.Id).FirstAsync(Ct)));

        var first = await h.Db.History<Policy>().Where(v => v.Entity.Id == 1).ToListAsync(Ct); // newest first
        Assert.Equal(
            [VersionOperation.Delete, VersionOperation.Update, VersionOperation.Update, VersionOperation.Insert],
            first.Select(v => v.Operation));
        for (var i = first.Count - 1; i > 0; i--)
        {
            Assert.Equal(first[i].ValidTo, first[i - 1].ValidFrom);
        }

        Assert.DoesNotContain(first, v => v.IsCurrent);
        Assert.Single(await h.Db.History<Policy>().ToListAsync(Ct), v => v.IsCurrent);

        // New history_id values continue after the copied ones.
        var ids = (await ReadRowsAsync(h.ConnectionString)).Select(r => r.HistoryId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Every open version sits in the current partition and nothing else does.
        foreach (var row in await ReadRowsAsync(h.ConnectionString))
        {
            Assert.Equal(row.ValidTo == DateTime.MaxValue, row.Partition == "policies_history_current");
        }

        Assert.Equal(
            ["PK_policies_history", "ix_policies_history_period", "ix_policies_history_version"],
            await ParentIndexesAsync(h.ConnectionString));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(17)]
    public async Task AsOf_on_partitioned_history_skips_partitions_that_ended_before_the_instant(int major)
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, major, "partition_pruning");
        await ConvertAsync(h.ConnectionString);
        await h.StepAsync(db => db.Policies.Add(new Policy { Status = "Draft" }));
        await h.StepAsync(async db => (await db.Policies.SingleAsync(Ct)).Status = "Active");

        var at = h.Now;
        var query = h.Db.Policies.AsOf(at);
        Assert.Equal(["Active"], await query.Select(p => p.Status).ToListAsync(Ct));

        var plan = await ExplainAsync(h, query);
        var lastYear = "policies_history_" + at.AddMonths(-11).ToString("yyyy_MM", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("policies_history_current", plan, StringComparison.Ordinal);
        Assert.DoesNotContain(lastYear, plan, StringComparison.Ordinal);
    }

    // The Interceptor writer is the one that can date versions into last month; the retention mechanics
    // themselves do not depend on the writer (HistoryRetentionTests runs them under both).
    [Theory]
    [InlineData(14)]
    [InlineData(17)]
    public async Task Detaching_a_partition_and_pruning_in_one_transaction_records_the_horizon(int major)
    {
        var now = DateTimeOffset.UtcNow;
        var thisMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var lastMonth = thisMonth.AddMonths(-1);
        await using var h = await CreateAsync(HistoryWriter.Interceptor, major, "partition_detach", start: lastMonth);
        await ConvertAsync(h.ConnectionString);

        await h.StepAsync(db => db.Policies.Add(new Policy { Status = "Draft" }));          // [last month, …)
        await h.StepAsync(async db => (await db.Policies.SingleAsync(Ct)).Status = "Active"); // closes Draft last month
        h.MoveTo(thisMonth.AddHours(1));
        await h.StepAsync(async db => (await db.Policies.SingleAsync(Ct)).Status = "Cancelled"); // closes Active this month

        var partition = "policies_history_" + lastMonth.ToString("yyyy_MM", System.Globalization.CultureInfo.InvariantCulture);
        await using (var tx = await h.Db.Database.BeginTransactionAsync(Ct))
        {
            var detach = "ALTER TABLE policies_history DETACH PARTITION " + partition;
            await h.Db.Database.ExecuteSqlRawAsync(detach, Ct);
            Assert.Equal(0, await h.Db.PruneHistoryAsync<Policy>(thisMonth, cancellationToken: Ct));
            await tx.CommitAsync(Ct);
        }

        var drop = "DROP TABLE " + partition;
        await h.Db.Database.ExecuteSqlRawAsync(drop, Ct);

        Assert.Equal(thisMonth, await h.Db.GetHistoryHorizonAsync<Policy>(Ct));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => h.Db.Policies.AsOf(lastMonth.AddHours(1.5)).ToListAsync(Ct));
        Assert.Equal("HS001", ex.SqlState);
        Assert.Equal(["Active"], await h.Db.Policies.AsOf(thisMonth).Select(p => p.Status).ToListAsync(Ct));
        Assert.Equal(["Cancelled", "Active"], await h.Db.Policies.AllVersions().Select(p => p.Status).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Concurrent_updates_on_partitioned_history_stay_contiguous(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, 17, "partition_concurrent");
        await h.StepAsync(db => db.Policies.Add(new Policy { Status = "Draft" }));
        await ConvertAsync(h.ConnectionString);

        // Two overlapping transactions on the same row: the second blocks on the first's row lock, then
        // closes the version the first one just moved out of the current partition.
        await using var a = h.NewContext();
        await using var b = h.NewContext();
        await using var txA = await a.Database.BeginTransactionAsync(Ct);
        (await a.Policies.SingleAsync(Ct)).Status = "A";
        await a.SaveChangesAsync(Ct);

        var bWork = Task.Run(
            async () =>
            {
                await using var txB = await b.Database.BeginTransactionAsync(Ct);
                (await b.Policies.SingleAsync(Ct)).Status = "B";
                await b.SaveChangesAsync(Ct);
                await txB.CommitAsync(Ct);
            },
            Ct);

        await Task.Delay(200, Ct);
        await txA.CommitAsync(Ct);
        await bWork;

        var versions = await h.Db.History<Policy>().ToListAsync(Ct); // newest first
        Assert.Equal(["B", "A", "Draft"], versions.Select(v => v.Entity.Status));
        for (var i = versions.Count - 1; i > 0; i--)
        {
            Assert.Equal(versions[i].ValidTo, versions[i - 1].ValidFrom);
            Assert.True(versions[i].ValidTo > versions[i].ValidFrom);
        }

        Assert.Single(versions, v => v.IsCurrent);
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task ConvertAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = await File.ReadAllTextAsync(SnippetPath(), Ct);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static string SnippetPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "snippets", "PartitionHistory.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs/snippets/PartitionHistory.sql not found above the test output directory.");
    }

    private static async Task<bool> IsPartitionedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT relkind::text FROM pg_class WHERE oid = 'policies_history'::regclass";
        return (string?)await cmd.ExecuteScalarAsync(Ct) == "p";
    }

    private static async Task<List<string>> ParentIndexesAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT indexname FROM pg_indexes WHERE tablename = 'policies_history' ORDER BY indexname";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<HistoryRow>> ReadRowsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT history_id, id, status, valid_from, valid_to, operation, tableoid::regclass::text "
            + "FROM policies_history ORDER BY history_id";
        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTime>(3),
                reader.GetFieldValue<DateTime>(4),
                reader.GetInt16(5),
                reader.GetString(6)));
        }

        return rows;
    }

    private static async Task<string> ExplainAsync(Harness h, IQueryable<Policy> query)
    {
        await using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN " + command.CommandText;
        await h.Db.Database.OpenConnectionAsync(Ct);
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        await h.Db.Database.CloseConnectionAsync();
        return string.Join('\n', plan);
    }

    private async Task<Harness> CreateAsync(HistoryWriter writer, int major, string dbName, DateTimeOffset? start = null)
    {
        var fixture = major == 14 ? postgres14 : postgres17;
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var cs = await fixture.CreateDatabaseAsync($"{dbName}_pg{major}{suffix}", Ct);

        var clockStart = start ?? DateTimeOffset.UtcNow.AddDays(-1);
        var time = new MutableTimeProvider(new DateTimeOffset(clockStart.Year, clockStart.Month, clockStart.Day, clockStart.Hour, 0, 0, TimeSpan.Zero));

        var h = new Harness(cs, writer, time);
        await h.Db.Database.EnsureCreatedAsync(Ct);
        return h;
    }

    private sealed record HistoryRow(
        long HistoryId, int Id, string Status, DateTime ValidFrom, DateTime ValidTo, short Operation, string Partition);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HistoryWriter _writer;
        private readonly MutableTimeProvider _time;

        public Harness(string connectionString, HistoryWriter writer, MutableTimeProvider time)
        {
            ConnectionString = connectionString;
            _writer = writer;
            _time = time;
            Db = NewContext();
        }

        public string ConnectionString { get; }

        public PolicyContext Db { get; }

        public DateTimeOffset Now => _time.GetUtcNow();

        public PolicyContext NewContext()
            => new(new DbContextOptionsBuilder<PolicyContext>()
                .UseNpgsql(ConnectionString)
                .EnableServiceProviderCaching(false)
                .UseApplicationServiceProvider(new SingleServiceProvider(typeof(TimeProvider), _time))
                .UseHindsight(hb => hb.UseHistoryWriter(_writer))
                .Options);

        public void MoveTo(DateTimeOffset instant) => _time.Set(instant);

        public async Task StepAsync(Func<PolicyContext, Task> change)
        {
            _time.Set(_time.GetUtcNow().AddHours(1));
            await change(Db);
            await Db.SaveChangesAsync(Ct);
            Db.ChangeTracker.Clear();
        }

        public Task StepAsync(Action<PolicyContext> change)
            => StepAsync(db =>
            {
                change(db);
                return Task.CompletedTask;
            });

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type type) => type == serviceType ? instance : null;
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTimeOffset instant) => _now = instant;
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Status).HasColumnName("status");
            policy.IsTemporal(t => t.WithRetention());
        }
    }
}
