using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="HindsightQueryableExtensions.AllVersions{TEntity}"/> on a real
/// PostgreSQL (DESIGN.md D12). History is seeded through <c>SaveChanges</c> with time injected via
/// <see cref="TimeProvider"/> (.claude/rules/tests.md). Assertions are on the set and order of
/// versions returned and on the shape of the single SQL query produced, never on wall-clock timing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AllVersionsQueryTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllVersions_returns_every_stored_version_newest_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_returns_every_stored_version_newest_first));

        var versions = await h.Db.Policies.AllVersions().ToListAsync(Ct);

        Assert.Equal(["Cancelled", "Active", "Draft"], versions.Select(p => p.Status));
        Assert.Equal([250.50m, 250.50m, 100m], versions.Select(p => p.Premium));
    }

    [Fact]
    public async Task AllVersions_composes_with_Where_and_Select_as_one_history_query()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_composes_with_Where_and_Select_as_one_history_query));

        var statuses = await h.Db.Policies
            .AllVersions()
            .Where(p => p.Premium > 100m)
            .Select(p => p.Status)
            .ToListAsync(Ct);

        Assert.Equal(["Cancelled", "Active"], statuses);

        var sql = Assert.Single(h.Sql.Commands);
        Assert.Contains("policies_history", sql, StringComparison.Ordinal);
        Assert.Contains("valid_from", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllVersions_composes_with_Count()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_composes_with_Count));

        Assert.Equal(3, await h.Db.Policies.AllVersions().CountAsync(Ct));
    }

    [Fact]
    public async Task AllVersions_default_newest_first_order_is_replaced_by_a_user_OrderBy()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_default_newest_first_order_is_replaced_by_a_user_OrderBy));

        var statuses = await h.Db.Policies
            .AllVersions()
            .OrderBy(p => p.Premium)
            .ThenBy(p => p.Status)
            .Select(p => p.Status)
            .ToListAsync(Ct);

        // Premiums are 100 / 250.50 / 250.50; a caller OrderBy replaces the baked-in valid_from desc.
        Assert.Equal(["Draft", "Active", "Cancelled"], statuses);
    }

    [Fact]
    public async Task AllVersions_excludes_the_delete_tombstone_and_keeps_the_timeline_up_to_deletion()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_excludes_the_delete_tombstone_and_keeps_the_timeline_up_to_deletion));

        h.Time.Advance(TimeSpan.FromHours(1));
        h.Db.Policies.Remove(await h.Db.Policies.SingleAsync(Ct));
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await h.Db.Policies.AllVersions().ToListAsync(Ct);

        // Still the three real states; the operation = 3 tombstone is not a version.
        Assert.Equal(["Cancelled", "Active", "Draft"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task AllVersions_results_are_not_tracked()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_results_are_not_tracked));

        var versions = await h.Db.Policies.AllVersions().ToListAsync(Ct);

        Assert.Empty(h.Db.ChangeTracker.Entries());
        Assert.All(versions, p => Assert.Equal(EntityState.Detached, h.Db.Entry(p).State));
    }

    [Fact]
    public async Task AllVersions_handles_a_composite_key_entity()
    {
        await using var h = await CreateAsync(nameof(AllVersions_handles_a_composite_key_entity));

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        h.Db.Readings.Add(new Reading { SensorId = 2, Sequence = 1, Value = 20 });
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.Db.Readings.Single(r => r.SensorId == 1).Value = 11;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var sensorOne = await h.Db.Readings
            .AllVersions()
            .Where(r => r.SensorId == 1)
            .ToListAsync(Ct);

        Assert.Equal([11d, 10d], sensorOne.Select(r => r.Value));
    }

    [Fact]
    public async Task AllVersions_reconstructs_enum_jsonb_and_array_columns()
    {
        await using var h = await CreateAsync(nameof(AllVersions_reconstructs_enum_jsonb_and_array_columns));

        var policy = NewPolicy();
        policy.Tags = ["gold"];
        policy.Metadata = """{"broker":"kb"}""";
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Tags = ["gold", "renewal"];
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await h.Db.Policies.AllVersions().ToListAsync(Ct);

        Assert.Equal(["gold", "renewal"], versions[0].Tags);
        Assert.Equal(["gold"], versions[1].Tags);
        Assert.All(versions, p => Assert.Equal("""{"broker": "kb"}""", p.Metadata)); // jsonb normalises whitespace
    }

    [Fact]
    public async Task AllVersions_composes_with_AsOf_in_the_same_query_tree()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_composes_with_AsOf_in_the_same_query_tree));

        var statuses = await h.Db.Policies.AllVersions().Select(p => p.Status)
            .Concat(h.Db.Policies.AsOf(_t0.AddMinutes(1)).Select(p => p.Status))
            .OrderBy(s => s)
            .ToListAsync(Ct);

        Assert.Equal(["Active", "Cancelled", "Draft", "Draft"], statuses);
    }

    [Fact]
    public async Task AllVersions_after_another_operator_throws_and_says_to_move_it_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_after_another_operator_throws_and_says_to_move_it_first));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.Where(p => p.Premium > 0m).AllVersions().ToListAsync(Ct));

        Assert.Contains("first operator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllVersions_on_a_non_temporal_entity_throws_InvalidOperation_naming_the_entity()
    {
        await using var h = await CreateAsync(nameof(AllVersions_on_a_non_temporal_entity_throws_InvalidOperation_naming_the_entity));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Plain.AllVersions().ToListAsync(Ct));

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsTemporal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllVersions_with_Include_throws_NotSupported_naming_the_D8_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_with_Include_throws_NotSupported_naming_the_D8_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.AllVersions().Include(p => p.Notes).ToListAsync(Ct));

        Assert.Contains("Include", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllVersions_with_AsTracking_throws_because_historical_results_are_no_tracking()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_with_AsTracking_throws_because_historical_results_are_no_tracking));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.AllVersions().AsTracking().ToListAsync(Ct));

        Assert.Contains("no-tracking", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllVersions_generates_the_expected_sql()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AllVersions_generates_the_expected_sql));

        var sql = h.Db.Policies
            .AllVersions()
            .Where(p => p.Status == "Active")
            .ToQueryString();

        await Verify(sql, extension: "sql");
    }

    // ---------------------------------------------------------------------------------------------

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = "Draft", Premium = 100m };

    private async Task<Harness> SeedThreeVersionsAsync(string dbName)
    {
        var h = await CreateAsync(dbName);

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

    private async Task<Harness> CreateAsync(string dbName)
    {
        var cs = await postgres.CreateDatabaseAsync(dbName, Ct);
        var time = new MutableTimeProvider(_t0);
        var sql = new SqlCapture();

        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(new SingleServiceProvider(typeof(TimeProvider), time))
            .UseHindsight(hb => hb.UseHistoryWriter(HistoryWriter.Interceptor))
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

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type type) => type == serviceType ? instance : null;
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
