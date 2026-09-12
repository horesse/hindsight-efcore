using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="HindsightQueryableExtensions.History{TEntity}"/> on a real
/// PostgreSQL (DESIGN.md D12). Like the <see cref="AsOfQueryTests"/> / <see cref="AllVersionsQueryTests"/>
/// batteries, history is seeded through <c>SaveChanges</c> with time injected via
/// <see cref="TimeProvider"/> (.claude/rules/tests.md). Assertions are on the set / order of versions,
/// their <see cref="Version{TEntity}"/> metadata, the interval shape, and the single SQL query
/// produced — never on wall-clock timing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryQueryTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_returns_every_row_including_the_delete_tombstone_newest_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_returns_every_row_including_the_delete_tombstone_newest_first));

        h.Time.Advance(TimeSpan.FromHours(1));
        h.Db.Policies.Remove(await h.Db.Policies.SingleAsync(Ct));
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await h.Db.History<Policy>().ToListAsync(Ct);

        Assert.Equal(
            [VersionOperation.Delete, VersionOperation.Update, VersionOperation.Update, VersionOperation.Insert],
            versions.Select(v => v.Operation));
        Assert.Equal(["Cancelled", "Cancelled", "Active", "Draft"], versions.Select(v => v.Entity.Status));
    }

    [Fact]
    public async Task History_version_periods_are_contiguous_and_only_the_open_one_IsCurrent()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_version_periods_are_contiguous_and_only_the_open_one_IsCurrent));

        var versions = await h.Db.History<Policy>().ToListAsync(Ct); // newest first

        // prev.ValidTo == next.ValidFrom walking oldest -> newest, no gaps, no overlaps.
        for (var i = versions.Count - 1; i > 0; i--)
        {
            Assert.Equal(versions[i].ValidTo, versions[i - 1].ValidFrom);
        }

        Assert.True(versions[0].IsCurrent);
        Assert.Equal(DateTimeOffset.MaxValue, versions[0].ValidTo);
        Assert.All(versions.Skip(1), v => Assert.False(v.IsCurrent));
    }

    [Fact]
    public async Task History_tombstone_has_an_empty_interval_and_is_never_IsCurrent()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_tombstone_has_an_empty_interval_and_is_never_IsCurrent));

        h.Time.Advance(TimeSpan.FromHours(1));
        h.Db.Policies.Remove(await h.Db.Policies.SingleAsync(Ct));
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var tombstone = (await h.Db.History<Policy>().ToListAsync(Ct))[0];

        Assert.Equal(VersionOperation.Delete, tombstone.Operation);
        Assert.Equal(tombstone.ValidFrom, tombstone.ValidTo); // [ts, ts)
        Assert.False(tombstone.IsCurrent);
    }

    [Fact]
    public async Task History_composes_with_a_Where_on_an_entity_property_as_one_history_query()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_composes_with_a_Where_on_an_entity_property_as_one_history_query));

        var statuses = await h.Db.History<Policy>()
            .Where(v => v.Entity.Id == 1 && v.Entity.Premium > 100m)
            .Select(v => v.Entity.Status)
            .ToListAsync(Ct);

        Assert.Equal(["Cancelled", "Active"], statuses);

        var sql = Assert.Single(h.Sql.Commands);
        Assert.Contains("policies_history", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_composes_with_a_Select_mixing_metadata_and_entity_properties()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_composes_with_a_Select_mixing_metadata_and_entity_properties));

        var rows = await h.Db.History<Policy>()
            .Where(v => v.Entity.Id == 1)
            .Select(v => new { v.ValidFrom, v.ValidTo, v.Operation, v.Entity.Status })
            .ToListAsync(Ct);

        Assert.Equal(
            [VersionOperation.Update, VersionOperation.Update, VersionOperation.Insert],
            rows.Select(r => r.Operation));
        Assert.Equal(["Cancelled", "Active", "Draft"], rows.Select(r => r.Status));

        var sql = Assert.Single(h.Sql.Commands); // no client evaluation, one round trip
        Assert.Contains("policies_history", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_default_newest_first_order_is_replaced_by_a_user_OrderBy()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_default_newest_first_order_is_replaced_by_a_user_OrderBy));

        var statuses = await h.Db.History<Policy>()
            .OrderBy(v => v.ValidFrom)
            .Select(v => v.Entity.Status)
            .ToListAsync(Ct);

        Assert.Equal(["Draft", "Active", "Cancelled"], statuses);
    }

    [Fact]
    public async Task History_populates_the_change_context_columns_from_the_registered_provider()
    {
        var current = new ChangeContext { UserId = "u1", UserName = "Ada", CorrelationId = "c1", Reason = "created" };
        var provider = new RecordingChangeContextProvider(() => current);
        await using var h = await CreateAsync(
            nameof(History_populates_the_change_context_columns_from_the_registered_provider), provider);

        var policy = NewPolicy();
        policy.Metadata = """{"ip":"10.0.0.1"}""";
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromHours(1));
        current = new ChangeContext { UserId = "u2", UserName = "Boole", CorrelationId = "c2", Reason = "adjusted" };
        policy.Premium = 200m;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await h.Db.History<Policy>().ToListAsync(Ct); // [adjusted, created]

        Assert.Equal(["u2", "u1"], versions.Select(v => v.ChangedBy));
        Assert.Equal(["Boole", "Ada"], versions.Select(v => v.ChangedByName));
        Assert.Equal(["c2", "c1"], versions.Select(v => v.CorrelationId));
        Assert.Equal(["adjusted", "created"], versions.Select(v => v.Reason));
    }

    [Fact]
    public async Task History_tombstone_carries_the_delete_context()
    {
        var provider = new RecordingChangeContextProvider(
            () => new ChangeContext { UserId = "remover", Reason = "gdpr erasure" });
        await using var h = await CreateAsync(nameof(History_tombstone_carries_the_delete_context), provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromHours(2));
        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var tombstone = (await h.Db.History<Policy>().ToListAsync(Ct))[0];

        Assert.Equal(VersionOperation.Delete, tombstone.Operation);
        Assert.Equal("remover", tombstone.ChangedBy);
        Assert.Equal("gdpr erasure", tombstone.Reason);
    }

    [Fact]
    public async Task History_handles_a_composite_key_entity()
    {
        await using var h = await CreateAsync(nameof(History_handles_a_composite_key_entity), provider: null);

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        h.Db.Readings.Add(new Reading { SensorId = 2, Sequence = 1, Value = 20 });
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.Db.Readings.Single(r => r.SensorId == 1).Value = 11;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var sensorOne = await h.Db.History<Reading>()
            .Where(v => v.Entity.SensorId == 1)
            .ToListAsync(Ct);

        Assert.Equal([VersionOperation.Update, VersionOperation.Insert], sensorOne.Select(v => v.Operation));
        Assert.Equal([11d, 10d], sensorOne.Select(v => v.Entity.Value));
    }

    [Fact]
    public async Task History_reconstructs_enum_jsonb_and_array_columns_on_the_entity_snapshot()
    {
        await using var h = await CreateAsync(
            nameof(History_reconstructs_enum_jsonb_and_array_columns_on_the_entity_snapshot), provider: null);

        var policy = NewPolicy();
        policy.Tags = ["gold"];
        policy.Metadata = """{"broker":"kb"}""";
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Tags = ["gold", "renewal"];
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await h.Db.History<Policy>().ToListAsync(Ct);

        Assert.Equal(["gold", "renewal"], versions[0].Entity.Tags);
        Assert.Equal(["gold"], versions[1].Entity.Tags);
        Assert.All(versions, v => Assert.Equal("""{"broker": "kb"}""", v.Entity.Metadata)); // jsonb normalises whitespace
    }

    [Fact]
    public async Task History_results_are_not_tracked()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_results_are_not_tracked));

        var versions = await h.Db.History<Policy>().ToListAsync(Ct);

        Assert.Empty(h.Db.ChangeTracker.Entries());
        Assert.All(versions, v => Assert.Equal(EntityState.Detached, h.Db.Entry(v.Entity).State));
    }

    [Fact]
    public async Task History_on_a_non_temporal_entity_throws_InvalidOperation_naming_the_entity()
    {
        await using var h = await CreateAsync(
            nameof(History_on_a_non_temporal_entity_throws_InvalidOperation_naming_the_entity), provider: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.History<Widget>().ToListAsync(Ct));

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsTemporal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_with_Include_throws_NotSupported_naming_the_D8_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_with_Include_throws_NotSupported_naming_the_D8_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.History<Policy>().Include(v => v.Entity.Notes).ToListAsync(Ct));

        Assert.Contains("Include", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_with_AsTracking_throws_because_historical_results_are_no_tracking()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_with_AsTracking_throws_because_historical_results_are_no_tracking));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.History<Policy>().AsTracking().ToListAsync(Ct));

        Assert.Contains("no-tracking", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_with_ExecuteUpdate_throws_NotSupported_naming_the_D7_reason()
    {
        // Regression: without this guard, EF Core's own translator resolves this straight to an
        // UPDATE against policies_history (the audit trail), not an error and not the main table.
        await using var h = await SeedThreeVersionsAsync(nameof(History_with_ExecuteUpdate_throws_NotSupported_naming_the_D7_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.History<Policy>().ExecuteUpdateAsync(set => set.SetProperty(v => v.Reason, "Hacked"), Ct));

        Assert.Contains("ExecuteUpdate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_with_ExecuteDelete_throws_NotSupported_naming_the_D7_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_with_ExecuteDelete_throws_NotSupported_naming_the_D7_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.History<Policy>().ExecuteDeleteAsync(Ct));

        Assert.Contains("ExecuteDelete", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_ExecuteUpdate_rejection_writes_nothing_to_history()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_ExecuteUpdate_rejection_writes_nothing_to_history));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.History<Policy>().ExecuteUpdateAsync(set => set.SetProperty(v => v.Reason, "Hacked"), Ct));

        var versions = await h.Db.History<Policy>().ToListAsync(Ct);
        Assert.All(versions, v => Assert.Null(v.Reason));
    }

    [Fact]
    public async Task History_generates_the_expected_sql()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(History_generates_the_expected_sql));

        var sql = h.Db.History<Policy>()
            .Where(v => v.Entity.Status == "Active")
            .ToQueryString();

        await Verify(sql, extension: "sql");
    }

    // ---------------------------------------------------------------------------------------------

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = "Draft", Premium = 100m };

    private async Task<Harness> SeedThreeVersionsAsync(string dbName)
    {
        var h = await CreateAsync(dbName, provider: null);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);            // v1: Draft / 100, open at t0

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Status = "Active";
        policy.Premium = 250.50m;
        await h.Db.SaveChangesAsync(Ct);            // v2: Active / 250.50, open at t1

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Status = "Cancelled";
        await h.Db.SaveChangesAsync(Ct);            // v3: Cancelled / 250.50, open at t2

        h.Db.ChangeTracker.Clear();
        h.Sql.Commands.Clear();
        return h;
    }

    private async Task<Harness> CreateAsync(string dbName, RecordingChangeContextProvider? provider)
    {
        var cs = await postgres.CreateDatabaseAsync(dbName, Ct);
        var time = new MutableTimeProvider(_t0);
        var sql = new SqlCapture();

        var services = new Dictionary<Type, object> { [typeof(TimeProvider)] = time };
        if (provider is not null)
        {
            services[typeof(RecordingChangeContextProvider)] = provider;
        }

        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(new StubServiceProvider(services))
            .UseHindsight(hb =>
            {
                if (provider is not null)
                {
                    hb.WithChangeContext<RecordingChangeContextProvider>();
                }

                hb.UseHistoryWriter(HistoryWriter.Interceptor);
            })
            .AddInterceptors(sql)
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(db, time, sql);
    }

    private sealed class Harness(PolicyContext db, MutableTimeProvider time, SqlCapture sql) : IAsyncDisposable
    {
        public PolicyContext Db { get; } = db;

        public MutableTimeProvider Time { get; } = time;

        public SqlCapture Sql { get; } = sql;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingChangeContextProvider(Func<ChangeContext> factory) : IChangeContextProvider
    {
        public ChangeContext GetChangeContext(DbContext context) => factory();
    }

    private sealed class StubServiceProvider(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Premium { get; set; }
        public string[] Tags { get; set; } = [];
        public string Metadata { get; set; } = "{}";
        public DateTimeOffset UpdatedAt { get; set; }
        public List<Note> Notes { get; } = [];
    }

    private sealed class Note
    {
        public int Id { get; set; }
        public int PolicyId { get; set; }
        public string Text { get; set; } = "";
    }

    private sealed class Reading
    {
        public int SensorId { get; set; }
        public int Sequence { get; set; }
        public double Value { get; set; }
    }

    private sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        public DbSet<Reading> Readings => Set<Reading>();

        public DbSet<Widget> Plain => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.Property(p => p.Status).HasColumnName("status");
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.Property(p => p.Tags).HasColumnName("tags");
            policy.Property(p => p.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            policy.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            policy.HasMany(p => p.Notes).WithOne().HasForeignKey(n => n.PolicyId);
            policy.IsTemporal(t => t.Exclude(p => p.UpdatedAt));

            var note = modelBuilder.Entity<Note>();
            note.ToTable("notes");
            note.Property(n => n.Id).HasColumnName("id");
            note.Property(n => n.PolicyId).HasColumnName("policy_id");
            note.Property(n => n.Text).HasColumnName("text");

            var reading = modelBuilder.Entity<Reading>();
            reading.ToTable("readings");
            reading.HasKey(r => new { r.SensorId, r.Sequence });
            reading.Property(r => r.SensorId).HasColumnName("sensor_id");
            reading.Property(r => r.Sequence).HasColumnName("sequence");
            reading.Property(r => r.Value).HasColumnName("value");
            reading.IsTemporal();

            var widget = modelBuilder.Entity<Widget>();
            widget.ToTable("widgets");
            widget.Property(w => w.Id).HasColumnName("id");
            widget.Property(w => w.Name).HasColumnName("name");
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
