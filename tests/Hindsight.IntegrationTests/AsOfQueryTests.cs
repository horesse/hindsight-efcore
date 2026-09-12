using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="HindsightQueryableExtensions.AsOf{TEntity}"/> on a real PostgreSQL
/// (DESIGN.md D12). History is seeded through <c>SaveChanges</c> with time injected via
/// <see cref="TimeProvider"/> (.claude/rules/tests.md). Assertions are on the state returned for an
/// instant and on the shape of the single SQL query produced, never on wall-clock timing.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AsOfQueryTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _t1 = _t0.AddHours(1);
    private static readonly DateTimeOffset _t2 = _t0.AddHours(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AsOf_between_two_versions_returns_the_version_whose_period_contains_the_instant()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_between_two_versions_returns_the_version_whose_period_contains_the_instant));

        var policy = await h.Db.Policies.AsOf(_t1.AddMinutes(30)).SingleAsync(Ct);

        Assert.Equal("Active", policy.Status);
        Assert.Equal(250.50m, policy.Premium);
    }

    [Fact]
    public async Task AsOf_composes_with_Where_OrderBy_Select_and_First_as_one_history_query()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_composes_with_Where_OrderBy_Select_and_First_as_one_history_query));

        var status = await h.Db.Policies
            .AsOf(_t1.AddMinutes(30))
            .Where(p => p.Premium > 0m)
            .OrderBy(p => p.Number)
            .Select(p => p.Status)
            .FirstAsync(Ct);

        Assert.Equal("Active", status);

        var sql = Assert.Single(h.Sql.Commands);
        Assert.Contains("policies_history", sql, StringComparison.Ordinal);
        Assert.Contains("valid_from", sql, StringComparison.Ordinal);
        Assert.Contains("valid_to", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-01-01", sql, StringComparison.Ordinal); // the instant is a parameter, not a literal
    }

    [Fact]
    public async Task AsOf_before_the_first_version_returns_nothing()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_before_the_first_version_returns_nothing));

        Assert.False(await h.Db.Policies.AsOf(_t0.AddMinutes(-1)).AnyAsync(Ct));
    }

    [Fact]
    public async Task AsOf_exactly_on_a_version_boundary_returns_the_version_that_opens_there()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_exactly_on_a_version_boundary_returns_the_version_that_opens_there));

        // Half-open [valid_from, valid_to): the instant equal to valid_from belongs to the new version.
        var policy = await h.Db.Policies.AsOf(_t1).SingleAsync(Ct);

        Assert.Equal("Active", policy.Status);
    }

    [Fact]
    public async Task AsOf_never_returns_a_deleted_entity_even_at_the_delete_instant()
    {
        await using var h = await CreateAsync(nameof(AsOf_never_returns_a_deleted_entity_even_at_the_delete_instant));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromHours(1)); // delete at t1
        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);

        Assert.True(await h.Db.Policies.AsOf(_t0.AddMinutes(30)).AnyAsync(Ct)); // alive before the delete
        Assert.False(await h.Db.Policies.AsOf(_t1).AnyAsync(Ct));               // gone from the delete instant
        Assert.False(await h.Db.Policies.AsOf(_t2).AnyAsync(Ct));               // tombstone [t1, t1) never matches
    }

    [Fact]
    public async Task AsOf_reconstructs_enum_jsonb_and_array_columns()
    {
        await using var h = await CreateAsync(nameof(AsOf_reconstructs_enum_jsonb_and_array_columns));

        var policy = NewPolicy();
        policy.Tags = ["gold", "renewal"];
        policy.Metadata = """{"broker":"kb"}""";
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        var loaded = await h.Db.Policies.AsOf(_t0.AddMinutes(1)).SingleAsync(Ct);

        Assert.Equal("Draft", loaded.Status);                 // enum stored as text, like the main table
        Assert.Equal(["gold", "renewal"], loaded.Tags);
        Assert.Equal("""{"broker": "kb"}""", loaded.Metadata); // jsonb normalises whitespace
    }

    [Fact]
    public async Task AsOf_handles_a_composite_key_entity()
    {
        await using var h = await CreateAsync(nameof(AsOf_handles_a_composite_key_entity));

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        h.Db.Readings.Add(new Reading { SensorId = 2, Sequence = 1, Value = 20 });
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.Db.Readings.Single(r => r.SensorId == 1).Value = 11;
        await h.Db.SaveChangesAsync(Ct);

        var before = await h.Db.Readings.AsOf(_t0.AddMinutes(1)).OrderBy(r => r.SensorId).ToListAsync(Ct);
        var after = await h.Db.Readings.AsOf(_t0.AddMinutes(10)).OrderBy(r => r.SensorId).ToListAsync(Ct);

        Assert.Equal([10d, 20d], before.Select(r => r.Value));
        Assert.Equal([11d, 20d], after.Select(r => r.Value));
    }

    [Fact]
    public async Task AsOf_results_are_not_tracked()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_results_are_not_tracked));

        var policy = await h.Db.Policies.AsOf(_t2.AddMinutes(1)).SingleAsync(Ct);

        Assert.Empty(h.Db.ChangeTracker.Entries());
        Assert.Equal(EntityState.Detached, h.Db.Entry(policy).State);
    }

    [Fact]
    public async Task AsOf_composes_with_a_second_AsOf_at_a_different_instant()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_composes_with_a_second_AsOf_at_a_different_instant));

        var rows = await h.Db.Policies.AsOf(_t0.AddMinutes(1))
            .Concat(h.Db.Policies.AsOf(_t2.AddMinutes(1)))
            .OrderBy(p => p.Premium)
            .ToListAsync(Ct);

        Assert.Equal(["Draft", "Cancelled"], rows.Select(p => p.Status));
    }

    [Fact]
    public async Task AsOf_on_both_sides_of_a_join_translates()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_on_both_sides_of_a_join_translates));
        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 }); // valid_from = t2
        await h.Db.SaveChangesAsync(Ct);

        var at = _t2.AddMinutes(1);
        var rows = await (
            from p in h.Db.Policies.AsOf(at)
            join r in h.Db.Readings.AsOf(at) on p.Id equals r.SensorId
            select new { p.Number, r.Value }).ToListAsync(Ct);

        var only = Assert.Single(rows);
        Assert.Equal("ACME-1", only.Number);
        Assert.Equal(10d, only.Value);
    }

    [Fact]
    public async Task AsOf_projection_used_inside_a_subquery_translates()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_projection_used_inside_a_subquery_translates));

        var asOfNumbers = h.Db.Policies.AsOf(_t1).Select(p => p.Number);
        var currentMatches = await h.Db.Policies.Where(p => asOfNumbers.Contains(p.Number)).CountAsync(Ct);

        Assert.Equal(1, currentMatches);
    }

    [Fact]
    public async Task AsOf_with_Include_throws_NotSupported_naming_the_D8_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_with_Include_throws_NotSupported_naming_the_D8_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.AsOf(_t1).Include(p => p.Notes).ToListAsync(Ct));

        Assert.Contains("Include", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_with_AsTracking_throws_because_historical_results_are_no_tracking()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_with_AsTracking_throws_because_historical_results_are_no_tracking));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.AsOf(_t1).AsTracking().ToListAsync(Ct));

        Assert.Contains("no-tracking", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_with_ExecuteUpdate_throws_NotSupported_naming_the_D7_reason()
    {
        // Regression: without this guard, EF Core's own translator resolves this straight to an
        // UPDATE against policies_history (the audit trail), not an error and not the main table.
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_with_ExecuteUpdate_throws_NotSupported_naming_the_D7_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.AsOf(_t1).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, "Hacked"), Ct));

        Assert.Contains("ExecuteUpdate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_with_ExecuteDelete_throws_NotSupported_naming_the_D7_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_with_ExecuteDelete_throws_NotSupported_naming_the_D7_reason));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.AsOf(_t1).ExecuteDeleteAsync(Ct));

        Assert.Contains("ExecuteDelete", ex.Message, StringComparison.Ordinal);
        Assert.Contains("D7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_ExecuteUpdate_rejection_writes_nothing_to_history_or_the_main_table()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_ExecuteUpdate_rejection_writes_nothing_to_history_or_the_main_table));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.AsOf(_t1).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, "Hacked"), Ct));

        Assert.Equal("Cancelled", (await h.Db.Policies.SingleAsync(Ct)).Status);
        var versions = await h.Db.Policies.AllVersions().ToListAsync(Ct);
        Assert.Equal(["Cancelled", "Active", "Draft"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task AsOf_on_a_non_temporal_entity_throws_InvalidOperation_naming_the_entity()
    {
        await using var h = await CreateAsync(nameof(AsOf_on_a_non_temporal_entity_throws_InvalidOperation_naming_the_entity));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Plain.AsOf(_t1).ToListAsync(Ct));

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsTemporal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_after_another_operator_throws_and_says_to_move_it_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_after_another_operator_throws_and_says_to_move_it_first));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.Where(p => p.Premium > 0m).AsOf(_t1).ToListAsync(Ct));

        Assert.Contains("first operator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsOf_generates_the_expected_sql()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(AsOf_generates_the_expected_sql));

        var sql = h.Db.Policies
            .AsOf(_t1)
            .Where(p => p.Status == "Active")
            .OrderByDescending(p => p.Number)
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
        return new Harness(cs, db, time, sql);
    }

    private sealed class Harness(string connectionString, PolicyContext db, MutableTimeProvider time, SqlCapture sql)
        : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

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
