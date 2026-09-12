using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for the second half of DESIGN.md D7: an entity read from <c>AsOf()</c> /
/// <c>AllVersions()</c> / <c>History&lt;T&gt;()</c> is read-only, and re-attaching one and calling
/// <c>SaveChanges</c> throws instead of silently writing a stale snapshot back as the current version.
/// Shaped as a <c>[Theory]</c> over <see cref="HistoryWriter"/> so the trigger writer slots in later;
/// only <see cref="HistoryWriter.Interceptor"/> exists today (.claude/rules/tests.md). Time is injected
/// through <see cref="TimeProvider"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistorySnapshotGuardTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task AsOf_snapshot_then_Update_and_SaveChanges_throws_naming_the_entity(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(AsOf_snapshot_then_Update_and_SaveChanges_throws_naming_the_entity));

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        h.Db.Update(snapshot);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
        Assert.Contains("Policy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("historical query", ex.Message, StringComparison.Ordinal);
        Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task AllVersions_snapshot_then_Update_and_SaveChanges_throws(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(AllVersions_snapshot_then_Update_and_SaveChanges_throws));

        var snapshot = (await h.Db.Policies.AllVersions().ToListAsync(Ct))[^1]; // the oldest version
        snapshot.Premium = 999m;
        h.Db.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task History_version_Entity_then_Update_and_SaveChanges_throws(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(History_version_Entity_then_Update_and_SaveChanges_throws));

        var version = (await h.Db.History<Policy>().ToListAsync(Ct))[0];
        h.Db.Update(version.Entity);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
        Assert.Contains("Policy", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task AsOf_snapshot_attached_and_marked_Modified_throws(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(AsOf_snapshot_attached_and_marked_Modified_throws));

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        h.Db.Attach(snapshot);
        h.Db.Entry(snapshot).State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task AsOf_snapshot_then_Remove_and_SaveChanges_throws(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(AsOf_snapshot_then_Remove_and_SaveChanges_throws));

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        h.Db.Remove(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task AsOf_snapshot_then_Add_and_SaveChanges_throws(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(AsOf_snapshot_then_Add_and_SaveChanges_throws));
        h.Db.ChangeTracker.Clear();

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        snapshot.Id = 0; // pretend it is a new row
        h.Db.Add(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Snapshot_from_a_composed_AsOf_query_with_Where_is_still_guarded(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Snapshot_from_a_composed_AsOf_query_with_Where_is_still_guarded));

        var snapshot = await h.Db.Policies
            .AsOf(_t0.AddMinutes(30))
            .Where(p => p.Premium > 0m)
            .OrderBy(p => p.Number)
            .FirstAsync(Ct);
        h.Db.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Snapshot_from_AsOf_as_the_second_argument_of_Concat_is_still_guarded(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Snapshot_from_AsOf_as_the_second_argument_of_Concat_is_still_guarded));

        // The history query is arg[1] of Concat, not arg[0] — TryTagSequence's walk must not be
        // limited to arg[0]. The other side is a plain (non-history) query, shaped as an explicit
        // Select(p => new Policy { ... }) so it structurally matches the AsOf() projection — EF
        // Core's set-operation translation requires both sides of a Concat/Union to assign the same
        // properties, and a bare entity query root doesn't match a member-init projection.
        var rows = await h.Db.Policies.Where(p => false)
            .Select(p => new Policy { Id = p.Id, Number = p.Number, Status = p.Status, Premium = p.Premium })
            .Concat(h.Db.Policies.AsOf(_t0.AddMinutes(30)))
            .ToListAsync(Ct);
        var snapshot = Assert.Single(rows);
        h.Db.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Snapshot_from_AsOf_as_the_second_argument_of_Union_is_still_guarded(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Snapshot_from_AsOf_as_the_second_argument_of_Union_is_still_guarded));

        var rows = await h.Db.Policies.Where(p => false)
            .Select(p => new Policy { Id = p.Id, Number = p.Number, Status = p.Status, Premium = p.Premium })
            .Union(h.Db.Policies.AsOf(_t0.AddMinutes(30)))
            .ToListAsync(Ct);
        var snapshot = Assert.Single(rows);
        h.Db.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Snapshot_from_a_single_result_operator_with_a_predicate_is_still_guarded(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Snapshot_from_a_single_result_operator_with_a_predicate_is_still_guarded));

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(p => p.Id == 1, Ct);
        h.Db.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Snapshot_is_still_guarded_after_ChangeTracker_Clear_between_query_and_Update(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Snapshot_is_still_guarded_after_ChangeTracker_Clear_between_query_and_Update));

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        h.Db.ChangeTracker.Clear(); // the mark lives in a ConditionalWeakTable, not the tracker
        h.Db.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Guard_rejection_writes_nothing_to_the_database(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Guard_rejection_writes_nothing_to_the_database));

        var historyRowsBefore = await CountHistoryRowsAsync(h.ConnectionString);

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        snapshot.Status = "Tampered";
        h.Db.Update(snapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));

        h.Db.ChangeTracker.Clear();
        Assert.Equal(historyRowsBefore, await CountHistoryRowsAsync(h.ConnectionString));
        var live = await h.Db.Policies.SingleAsync(Ct);
        Assert.NotEqual("Tampered", live.Status);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Composite_key_AsOf_snapshot_is_guarded(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Composite_key_AsOf_snapshot_is_guarded), new(_t0));

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        await h.Db.SaveChangesAsync(Ct);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        (await h.Db.Readings.SingleAsync(Ct)).Value = 11;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var snapshot = await h.Db.Readings.AsOf(_t0.AddMinutes(1)).SingleAsync(Ct);
        h.Db.Update(snapshot);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
        Assert.Contains("Reading", ex.Message, StringComparison.Ordinal);
    }

    // ---- negatives: legitimate saves are untouched ---------------------------------------------

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task A_normal_load_edit_and_SaveChanges_is_unaffected(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(A_normal_load_edit_and_SaveChanges_is_unaffected));

        var live = await h.Db.Policies.SingleAsync(Ct);
        live.Premium += 1m;
        await h.Db.SaveChangesAsync(Ct); // no throw

        h.Db.ChangeTracker.Clear();
        Assert.Equal(251.50m, (await h.Db.Policies.SingleAsync(Ct)).Premium);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Values_copied_from_a_snapshot_onto_a_fresh_instance_save_normally(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Values_copied_from_a_snapshot_onto_a_fresh_instance_save_normally));

        var snapshot = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).SingleAsync(Ct);
        var restored = new Policy
        {
            Id = snapshot.Id,
            Number = snapshot.Number,
            Status = snapshot.Status, // the old "Draft" value, deliberately restored
            Premium = snapshot.Premium,
        };
        h.Db.Policies.Update(restored);
        await h.Db.SaveChangesAsync(Ct); // no throw — `restored` never came out of a history query

        h.Db.ChangeTracker.Clear();
        Assert.Equal("Draft", (await h.Db.Policies.SingleAsync(Ct)).Status);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Find_then_edit_then_SaveChanges_works(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(Find_then_edit_then_SaveChanges_works));

        var live = await h.Db.Policies.FindAsync([1], Ct);
        live!.Status = "Renewed";
        await h.Db.SaveChangesAsync(Ct); // no throw

        h.Db.ChangeTracker.Clear();
        Assert.Equal("Renewed", (await h.Db.Policies.SingleAsync(Ct)).Status);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task A_history_query_used_only_in_a_subquery_does_not_block_a_later_normal_save(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(A_history_query_used_only_in_a_subquery_does_not_block_a_later_normal_save));

        // The AsOf() result feeds a Contains() subquery; nothing about its rows is tagged.
        var pastNumbers = h.Db.Policies.AsOf(_t0.AddMinutes(30)).Select(p => p.Number);
        var numbers = await h.Db.Policies.Where(p => pastNumbers.Contains(p.Number)).Select(p => p.Number).ToListAsync(Ct);
        Assert.Equal(["ACME-1"], numbers);

        // A subsequent ordinary load + edit + save must still work.
        h.Db.ChangeTracker.Clear();
        var live = await h.Db.Policies.SingleAsync(Ct);
        live.Premium = 500m;
        await h.Db.SaveChangesAsync(Ct); // no throw

        h.Db.ChangeTracker.Clear();
        Assert.Equal(500m, (await h.Db.Policies.SingleAsync(Ct)).Premium);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task A_scalar_projection_off_AsOf_tags_nothing(HistoryWriter writer)
    {
        await using var h = await SeedTwoVersionsAsync(writer, nameof(A_scalar_projection_off_AsOf_tags_nothing));

        _ = await h.Db.Policies.AsOf(_t0.AddMinutes(30)).Select(p => p.Status).ToListAsync(Ct);

        var live = await h.Db.Policies.SingleAsync(Ct);
        live.Premium = 7m;
        await h.Db.SaveChangesAsync(Ct); // no throw

        h.Db.ChangeTracker.Clear();
        Assert.Equal(7m, (await h.Db.Policies.SingleAsync(Ct)).Premium);
    }

    // -------------------------------------------------------------------------------------------

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = "Draft", Premium = 100m };

    private async Task<Harness> SeedTwoVersionsAsync(HistoryWriter writer, string dbName)
    {
        var h = await CreateAsync(writer, dbName, new(_t0));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);        // v1: Draft / 100, open at t0

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Status = "Active";
        policy.Premium = 250.50m;
        await h.Db.SaveChangesAsync(Ct);        // v2: Active / 250.50, open at t1

        h.Db.ChangeTracker.Clear();
        return h;
    }

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName, MutableTimeProvider time)
    {
        var cs = await postgres.CreateDatabaseAsync(dbName, Ct);
        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(new SingleServiceProvider(typeof(TimeProvider), time))
            .UseHindsight(hb => hb.UseHistoryWriter(writer))
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db, time);
    }

    private static async Task<long> CountHistoryRowsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from policies_history";
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Harness(string connectionString, PolicyContext db, MutableTimeProvider time) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public PolicyContext Db { get; } = db;

        public MutableTimeProvider Time { get; } = time;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type type) => type == serviceType ? instance : null;
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Premium { get; set; }
    }

    private sealed class Reading
    {
        public int SensorId { get; set; }
        public int Sequence { get; set; }
        public double Value { get; set; }
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        public DbSet<Reading> Readings => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.Property(p => p.Status).HasColumnName("status");
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.IsTemporal();

            var reading = modelBuilder.Entity<Reading>();
            reading.ToTable("readings");
            reading.HasKey(r => new { r.SensorId, r.Sequence });
            reading.Property(r => r.SensorId).HasColumnName("sensor_id");
            reading.Property(r => r.Sequence).HasColumnName("sequence");
            reading.Property(r => r.Value).HasColumnName("value");
            reading.IsTemporal();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
