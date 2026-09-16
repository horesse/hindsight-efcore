using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// Both history writers rely on the same guarantee for concurrent writers to one row (DESIGN.md D3):
/// the data-table <c>UPDATE</c> takes the row lock for the whole transaction, so history writes on that
/// row serialize behind it. <see cref="InterceptorHistoryWriterTests.Concurrent_update_with_an_earlier_timestamp_stays_contiguous"/>
/// and <see cref="TriggerHistoryWriterTests.Concurrent_updates_in_separate_transactions_produce_contiguous_intervals"/>
/// pin down that guarantee with two carefully sequenced transactions. This fires many more concurrent
/// writers at the same row from separate connections, to shake out anything that only shows up under
/// real contention rather than a hand-scheduled race.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyStressTests(PostgresFixture postgres)
{
    private const int ConcurrentUpdates = 50;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Fifty_concurrent_updates_to_one_row_leave_a_well_formed_history_chain(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(DbName("fifty_concurrent_updates", writer), Ct);

        int id;
        await using (var seed = NewContext(cs, writer))
        {
            await seed.Database.EnsureCreatedAsync(Ct);
            var counter = new Counter { Value = 0 };
            seed.Counters.Add(counter);
            await seed.SaveChangesAsync(Ct);
            id = counter.Id;
        }

        var tasks = Enumerable.Range(1, ConcurrentUpdates).Select(async value =>
        {
            await using var db = NewContext(cs, writer);
            var counter = await db.Counters.SingleAsync(c => c.Id == id, Ct);
            counter.Value = value;
            await db.SaveChangesAsync(Ct);
        });

        await Task.WhenAll(tasks);

        var versions = await ReadHistoryAsync(cs, id);

        // One insert plus every one of the 50 updates landed as its own version: none lost, none
        // merged, none duplicated under contention.
        Assert.Equal(ConcurrentUpdates + 1, versions.Count);
        Assert.Equal((short)1, versions[0].Operation);
        Assert.All(versions.Skip(1), v => Assert.Equal((short)2, v.Operation));

        AssertWellFormedChain(versions);

        // Every update value shows up exactly once, in whatever order the writers actually committed —
        // nothing was silently overwritten by a lost update.
        Assert.Equal(
            Enumerable.Range(1, ConcurrentUpdates).OrderBy(v => v),
            versions.Skip(1).Select(v => v.Value).OrderBy(v => v));
    }

    // PostgreSQL truncates identifiers at 63 bytes; keep room for a per-writer suffix so the two
    // [Theory] runs do not collide on the database name (.claude/rules/tests.md).
    private static string DbName(string label, HistoryWriter writer)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = label.Length > 58 ? label[..58] : label;
        return trimmed + suffix;
    }

    private static CounterContext NewContext(string connectionString, HistoryWriter writer)
    {
        var options = new DbContextOptionsBuilder<CounterContext>()
            .UseNpgsql(connectionString)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        return new CounterContext(options);
    }

    private static async Task<List<HistoryRow>> ReadHistoryAsync(string connectionString, int id)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select value, valid_from, valid_to, operation
            from counters_history
            where id = @id
            order by valid_from, history_id
            """;
        cmd.Parameters.AddWithValue("id", id);

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetInt32(0),
                reader.GetFieldValue<DateTime>(1),
                reader.GetFieldValue<DateTime>(2),
                reader.GetInt16(3)));
        }

        return rows;
    }

    // prev.ValidTo == next.ValidFrom walking oldest -> newest: no gaps, no overlaps. Every interval is
    // strictly positive (there are no deletes here, so there is no legitimate empty tombstone). At most
    // one open version — never two "active" rows for the same entity at once.
    private static void AssertWellFormedChain(IReadOnlyList<HistoryRow> versions)
    {
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.Equal(versions[i - 1].ValidTo, versions[i].ValidFrom);
        }

        Assert.All(versions, v => Assert.True(v.ValidTo > v.ValidFrom, $"[{v.ValidFrom:O}, {v.ValidTo:O}) is not positive"));
        Assert.Single(versions, v => v.IsOpen);
    }

    private sealed record HistoryRow(int Value, DateTime ValidFrom, DateTime ValidTo, short Operation)
    {
        public bool IsOpen => ValidTo == DateTime.MaxValue;
    }

    private sealed class Counter
    {
        public int Id { get; set; }

        public int Value { get; set; }
    }

    private sealed class CounterContext(DbContextOptions<CounterContext> options) : DbContext(options)
    {
        public DbSet<Counter> Counters => Set<Counter>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var counter = modelBuilder.Entity<Counter>();
            counter.ToTable("counters");
            counter.Property(c => c.Id).HasColumnName("id");
            counter.Property(c => c.Value).HasColumnName("value");
            counter.IsTemporal();
        }
    }
}
