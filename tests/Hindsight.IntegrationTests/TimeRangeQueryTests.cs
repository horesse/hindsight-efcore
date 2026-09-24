using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="HindsightQueryableExtensions.FromTo{TEntity}"/> and
/// <see cref="HindsightQueryableExtensions.ContainedIn{TEntity}"/> on a real PostgreSQL (DESIGN.md D17).
/// Every test seeds the same timeline through <c>SaveChanges</c> with time injected via
/// <see cref="TimeProvider"/> (.claude/rules/tests.md):
/// <c>Draft [t0, t1)</c>, <c>Active [t1, t2)</c>, <c>Cancelled [t2, 'infinity')</c>, one hour apart,
/// and the window bounds are placed on and around those instants to pin every half-open boundary.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TimeRangeQueryTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _t1 = _t0.AddHours(1);
    private static readonly DateTimeOffset _t2 = _t0.AddHours(2);
    private static readonly DateTimeOffset _t3 = _t0.AddHours(3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- FromTo: overlap ---------------------------------------------------------------------------

    [Fact]
    public async Task FromTo_returns_every_version_valid_at_some_instant_of_the_window_newest_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_returns_every_version_valid_at_some_instant_of_the_window_newest_first));

        var versions = await h.Db.Policies.FromTo(_t0.AddMinutes(30), _t1.AddMinutes(30)).ToListAsync(Ct);

        Assert.Equal(["Active", "Draft"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_excludes_a_version_that_ended_exactly_at_from()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_excludes_a_version_that_ended_exactly_at_from));

        // Draft is [t0, t1): it was not valid at any instant of [t1, t1 + 30m).
        var versions = await h.Db.Policies.FromTo(_t1, _t1.AddMinutes(30)).ToListAsync(Ct);

        Assert.Equal(["Active"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_excludes_a_version_that_started_exactly_at_to()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_excludes_a_version_that_started_exactly_at_to));

        // Active is [t1, t2): the window [t0, t1) ends before it starts.
        var versions = await h.Db.Policies.FromTo(_t0, _t1).ToListAsync(Ct);

        Assert.Equal(["Draft"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_with_an_empty_window_returns_nothing_even_where_AsOf_finds_a_version()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_with_an_empty_window_returns_nothing_even_where_AsOf_finds_a_version));

        Assert.Empty(await h.Db.Policies.FromTo(_t1, _t1).ToListAsync(Ct));
        Assert.Empty(await h.Db.Policies.FromTo(_t1.AddMinutes(30), _t1.AddMinutes(30)).ToListAsync(Ct));
        Assert.Equal("Active", (await h.Db.Policies.AsOf(_t1.AddMinutes(30)).SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task FromTo_includes_the_current_version_when_it_started_before_to()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_includes_the_current_version_when_it_started_before_to));

        var versions = await h.Db.Policies.FromTo(_t3, _t3.AddHours(1)).ToListAsync(Ct);

        Assert.Equal(["Cancelled"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_before_the_first_version_returns_nothing()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_before_the_first_version_returns_nothing));

        Assert.Empty(await h.Db.Policies.FromTo(_t0.AddHours(-2), _t0).ToListAsync(Ct));
    }

    [Fact]
    public async Task FromTo_with_DateTimeOffset_MaxValue_as_to_is_unbounded()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_with_DateTimeOffset_MaxValue_as_to_is_unbounded));

        var versions = await h.Db.Policies.FromTo(_t0, DateTimeOffset.MaxValue).ToListAsync(Ct);

        Assert.Equal(["Cancelled", "Active", "Draft"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_excludes_the_delete_tombstone()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_excludes_the_delete_tombstone));
        await DeleteAtAsync(h, _t3);

        // Cancelled is now [t2, t3); the tombstone [t3, t3) is empty and not a version.
        Assert.Empty(await h.Db.Policies.FromTo(_t3, _t3.AddHours(1)).ToListAsync(Ct));
        Assert.Equal(
            ["Cancelled", "Active", "Draft"],
            (await h.Db.Policies.FromTo(_t0, DateTimeOffset.MaxValue).ToListAsync(Ct)).Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_converts_a_non_UTC_offset_to_UTC()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_converts_a_non_UTC_offset_to_UTC));
        var plusThree = TimeSpan.FromHours(3);

        // [t1, t1 + 30m) written in UTC+3.
        var versions = await h.Db.Policies
            .FromTo(_t1.ToOffset(plusThree), _t1.AddMinutes(30).ToOffset(plusThree))
            .ToListAsync(Ct);

        Assert.Equal(["Active"], versions.Select(p => p.Status));
    }

    // --- ContainedIn: containment ------------------------------------------------------------------

    [Fact]
    public async Task ContainedIn_includes_versions_that_start_exactly_at_from_and_end_exactly_at_to()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_includes_versions_that_start_exactly_at_from_and_end_exactly_at_to));

        // Draft [t0, t1) starts at from, Active [t1, t2) ends at to; Cancelled is still open.
        var versions = await h.Db.Policies.ContainedIn(_t0, _t2).ToListAsync(Ct);

        Assert.Equal(["Active", "Draft"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task ContainedIn_excludes_a_version_that_started_before_from()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_excludes_a_version_that_started_before_from));

        var versions = await h.Db.Policies.ContainedIn(_t0.AddMinutes(1), _t2).ToListAsync(Ct);

        Assert.Equal(["Active"], versions.Select(p => p.Status));
    }

    [Fact]
    public async Task ContainedIn_excludes_a_version_that_ended_after_to()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_excludes_a_version_that_ended_after_to));

        Assert.Empty(await h.Db.Policies.ContainedIn(_t1, _t2.AddMinutes(-1)).ToListAsync(Ct));
    }

    [Fact]
    public async Task ContainedIn_excludes_the_current_version_unless_to_is_DateTimeOffset_MaxValue()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_excludes_the_current_version_unless_to_is_DateTimeOffset_MaxValue));

        Assert.Equal(
            ["Active", "Draft"],
            (await h.Db.Policies.ContainedIn(_t0, _t3.AddYears(10)).ToListAsync(Ct)).Select(p => p.Status));
        Assert.Equal(
            ["Cancelled", "Active", "Draft"],
            (await h.Db.Policies.ContainedIn(_t0, DateTimeOffset.MaxValue).ToListAsync(Ct)).Select(p => p.Status));
    }

    [Fact]
    public async Task ContainedIn_with_an_empty_window_returns_nothing()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_with_an_empty_window_returns_nothing));

        Assert.Empty(await h.Db.Policies.ContainedIn(_t1, _t1).ToListAsync(Ct));
    }

    [Fact]
    public async Task ContainedIn_excludes_the_delete_tombstone_whose_empty_range_every_window_contains()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_excludes_the_delete_tombstone_whose_empty_range_every_window_contains));
        await DeleteAtAsync(h, _t3);

        // The tombstone is [t3, t3): an empty range, which PostgreSQL's <@ says is inside every range.
        Assert.Empty(await h.Db.Policies.ContainedIn(_t3, _t3.AddHours(1)).ToListAsync(Ct));
        Assert.Empty(await h.Db.Policies.ContainedIn(_t3, _t3).ToListAsync(Ct));

        // Cancelled is now closed at t3 and so contained; still three versions, not four.
        Assert.Equal(
            ["Cancelled", "Active", "Draft"],
            (await h.Db.Policies.ContainedIn(_t0, _t3).ToListAsync(Ct)).Select(p => p.Status));
    }

    // --- shared: argument validation, composition, parameterisation, index ---------------------------

    [Fact]
    public async Task FromTo_and_ContainedIn_throw_ArgumentOutOfRange_when_from_is_after_to()
    {
        await using var h = await CreateAsync(nameof(FromTo_and_ContainedIn_throw_ArgumentOutOfRange_when_from_is_after_to));

        var fromTo = Assert.Throws<ArgumentOutOfRangeException>(() => h.Db.Policies.FromTo(_t1, _t0));
        var containedIn = Assert.Throws<ArgumentOutOfRangeException>(() => h.Db.Policies.ContainedIn(_t1, _t0));

        Assert.Equal("from", fromTo.ParamName);
        Assert.Equal("from", containedIn.ParamName);
    }

    [Fact]
    public async Task FromTo_composes_with_Where_Select_and_Count_as_one_history_query()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_composes_with_Where_Select_and_Count_as_one_history_query));

        var statuses = await h.Db.Policies
            .FromTo(_t0, DateTimeOffset.MaxValue)
            .Where(p => p.Premium > 100m)
            .Select(p => p.Status)
            .ToListAsync(Ct);

        Assert.Equal(["Cancelled", "Active"], statuses);
        var sql = Assert.Single(h.Sql.Commands);
        Assert.Contains("FROM policies_history", sql, StringComparison.Ordinal);

        Assert.Equal(2, await h.Db.Policies.ContainedIn(_t0, _t2).CountAsync(Ct));
    }

    [Fact]
    public async Task FromTo_default_newest_first_order_is_replaced_by_a_user_OrderBy()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_default_newest_first_order_is_replaced_by_a_user_OrderBy));

        var statuses = await h.Db.Policies
            .FromTo(_t0, DateTimeOffset.MaxValue)
            .OrderBy(p => p.Status)
            .Select(p => p.Status)
            .ToListAsync(Ct);

        Assert.Equal(["Active", "Cancelled", "Draft"], statuses);
    }

    [Fact]
    public async Task FromTo_and_ContainedIn_compose_with_AsOf_in_the_same_query_tree()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_and_ContainedIn_compose_with_AsOf_in_the_same_query_tree));

        var statuses = await h.Db.Policies.FromTo(_t0, _t1).Select(p => p.Status)
            .Concat(h.Db.Policies.ContainedIn(_t1, _t2).Select(p => p.Status))
            .Concat(h.Db.Policies.AsOf(_t2).Select(p => p.Status))
            .OrderBy(s => s)
            .ToListAsync(Ct);

        Assert.Equal(["Active", "Cancelled", "Draft"], statuses);
    }

    [Fact]
    public async Task FromTo_bounds_are_query_parameters_so_every_window_shares_one_sql_text()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_bounds_are_query_parameters_so_every_window_shares_one_sql_text));

        await h.Db.Policies.FromTo(_t0, _t1).ToListAsync(Ct);
        await h.Db.Policies.FromTo(_t1, _t3).ToListAsync(Ct);

        Assert.Equal(2, h.Sql.Commands.Count);
        Assert.Equal(h.Sql.Commands[0], h.Sql.Commands[1]);
        Assert.Contains("tstzrange(@FromUtc, @ToUtc)", h.Sql.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromTo_results_are_not_tracked()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_results_are_not_tracked));

        var versions = await h.Db.Policies.FromTo(_t0, DateTimeOffset.MaxValue).ToListAsync(Ct);
        var contained = await h.Db.Policies.ContainedIn(_t0, _t2).ToListAsync(Ct);

        Assert.Empty(h.Db.ChangeTracker.Entries());
        Assert.All(versions.Concat(contained), p => Assert.Equal(EntityState.Detached, h.Db.Entry(p).State));
    }

    [Fact]
    public async Task A_FromTo_snapshot_cannot_be_saved_back()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(A_FromTo_snapshot_cannot_be_saved_back));

        var snapshot = await h.Db.Policies.FromTo(_t0, _t1).SingleAsync(Ct);
        h.Db.Policies.Update(snapshot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task FromTo_handles_a_composite_key_entity()
    {
        await using var h = await CreateAsync(nameof(FromTo_handles_a_composite_key_entity));

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        h.Db.Readings.Add(new Reading { SensorId = 2, Sequence = 1, Value = 20 });
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance(TimeSpan.FromHours(1));
        h.Db.Readings.Single(r => r.SensorId == 1).Value = 11;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var sensorOne = await h.Db.Readings.FromTo(_t0, DateTimeOffset.MaxValue).Where(r => r.SensorId == 1).ToListAsync(Ct);
        var closedInFirstHour = await h.Db.Readings.ContainedIn(_t0, _t1).ToListAsync(Ct);

        Assert.Equal([11d, 10d], sensorOne.Select(r => r.Value));
        Assert.Equal([10d], closedInFirstHour.Select(r => r.Value));
    }

    [Theory]
    [InlineData("FromTo")]
    [InlineData("ContainedIn")]
    public async Task Range_predicate_can_be_served_by_the_period_range_gist_index(string @operator)
    {
        await using var h = await SeedThreeVersionsAsync("range_gist_" + @operator);

        var query = @operator == "FromTo"
            ? h.Db.Policies.FromTo(_t0, _t1)
            : h.Db.Policies.ContainedIn(_t0, _t1);

        // With three rows the planner rightly prefers a sequential scan; switching it off shows the
        // index is applicable to the predicate, which is what the rewrite is responsible for.
        await h.Db.Database.OpenConnectionAsync(Ct);
        await using var tx = await h.Db.Database.BeginTransactionAsync(Ct);
        await h.Db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", Ct);

        await using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN " + command.CommandText;
        command.Transaction = tx.GetDbTransaction();

        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        var text = string.Join('\n', plan);
        Assert.Contains("ix_policies_history_period", text, StringComparison.Ordinal);
    }

    // --- guards (shared with AsOf / AllVersions) -----------------------------------------------------

    [Fact]
    public async Task FromTo_after_another_operator_throws_and_says_to_move_it_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_after_another_operator_throws_and_says_to_move_it_first));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.Where(p => p.Premium > 0m).FromTo(_t0, _t1).ToListAsync(Ct));

        Assert.Contains("FromTo() must be the first operator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContainedIn_after_another_operator_throws_and_says_to_move_it_first()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_after_another_operator_throws_and_says_to_move_it_first));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.OrderBy(p => p.Id).ContainedIn(_t0, _t1).ToListAsync(Ct));

        Assert.Contains("ContainedIn() must be the first operator", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromTo_and_ContainedIn_on_a_non_temporal_entity_throw_InvalidOperation_naming_the_entity()
    {
        await using var h = await CreateAsync(nameof(FromTo_and_ContainedIn_on_a_non_temporal_entity_throw_InvalidOperation_naming_the_entity));

        var fromTo = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.Plain.FromTo(_t0, _t1).ToListAsync(Ct));
        var containedIn = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.Plain.ContainedIn(_t0, _t1).ToListAsync(Ct));

        Assert.All([fromTo, containedIn], ex =>
        {
            Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
            Assert.Contains("IsTemporal", ex.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FromTo_and_ContainedIn_with_Include_throw_NotSupported_naming_the_D8_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_and_ContainedIn_with_Include_throw_NotSupported_naming_the_D8_reason));

        var fromTo = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.FromTo(_t0, _t1).Include(p => p.Notes).ToListAsync(Ct));
        var containedIn = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.ContainedIn(_t0, _t1).Include(p => p.Notes).ToListAsync(Ct));

        Assert.All([fromTo, containedIn], ex =>
        {
            Assert.Contains("Include", ex.Message, StringComparison.Ordinal);
            Assert.Contains("D8", ex.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FromTo_and_ContainedIn_with_AsTracking_throw_because_historical_results_are_no_tracking()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_and_ContainedIn_with_AsTracking_throw_because_historical_results_are_no_tracking));

        var fromTo = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.FromTo(_t0, _t1).AsTracking().ToListAsync(Ct));
        var containedIn = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.Policies.ContainedIn(_t0, _t1).AsTracking().ToListAsync(Ct));

        Assert.All([fromTo, containedIn], ex => Assert.Contains("no-tracking", ex.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FromTo_and_ContainedIn_with_ExecuteUpdate_throw_and_write_nothing()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_and_ContainedIn_with_ExecuteUpdate_throw_and_write_nothing));

        var fromTo = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.FromTo(_t0, DateTimeOffset.MaxValue).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, "Hacked"), Ct));
        var containedIn = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.ContainedIn(_t0, DateTimeOffset.MaxValue).ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, "Hacked"), Ct));

        Assert.All([fromTo, containedIn], ex =>
        {
            Assert.Contains("ExecuteUpdate", ex.Message, StringComparison.Ordinal);
            Assert.Contains("D7", ex.Message, StringComparison.Ordinal);
        });
        Assert.Equal("Cancelled", (await h.Db.Policies.SingleAsync(Ct)).Status);
        Assert.Equal(
            ["Cancelled", "Active", "Draft"],
            (await h.Db.Policies.AllVersions().ToListAsync(Ct)).Select(p => p.Status));
    }

    [Fact]
    public async Task FromTo_and_ContainedIn_with_ExecuteDelete_throw_NotSupported_naming_the_D7_reason()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_and_ContainedIn_with_ExecuteDelete_throw_NotSupported_naming_the_D7_reason));

        var fromTo = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.FromTo(_t0, _t1).ExecuteDeleteAsync(Ct));
        var containedIn = await Assert.ThrowsAsync<NotSupportedException>(() =>
            h.Db.Policies.ContainedIn(_t0, _t1).ExecuteDeleteAsync(Ct));

        Assert.All([fromTo, containedIn], ex =>
        {
            Assert.Contains("ExecuteDelete", ex.Message, StringComparison.Ordinal);
            Assert.Contains("D7", ex.Message, StringComparison.Ordinal);
        });
    }

    // --- canonical SQL -----------------------------------------------------------------------------

    [Fact]
    public async Task FromTo_generates_the_expected_sql()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(FromTo_generates_the_expected_sql));

        var sql = h.Db.Policies
            .FromTo(_t0, _t1)
            .Where(p => p.Status == "Active")
            .ToQueryString();

        await Verify(sql, extension: "sql");
    }

    [Fact]
    public async Task ContainedIn_generates_the_expected_sql()
    {
        await using var h = await SeedThreeVersionsAsync(nameof(ContainedIn_generates_the_expected_sql));

        var sql = h.Db.Policies
            .ContainedIn(_t0, _t1)
            .Where(p => p.Status == "Active")
            .ToQueryString();

        await Verify(sql, extension: "sql");
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task DeleteAtAsync(Harness h, DateTimeOffset at)
    {
        h.Time.Advance(at - h.Time.GetUtcNow());
        h.Db.Policies.Remove(await h.Db.Policies.SingleAsync(Ct));
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();
    }

    private async Task<Harness> SeedThreeVersionsAsync(string dbName)
    {
        var h = await CreateAsync(dbName);

        var policy = new Policy { Number = "ACME-1", Status = "Draft", Premium = 100m };
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);            // Draft / 100:        [t0, t1)

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Status = "Active";
        policy.Premium = 250.50m;
        await h.Db.SaveChangesAsync(Ct);            // Active / 250.50:    [t1, t2)

        h.Time.Advance(TimeSpan.FromHours(1));
        policy.Status = "Cancelled";
        await h.Db.SaveChangesAsync(Ct);            // Cancelled / 250.50: [t2, 'infinity')

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
            .EnableServiceProviderCaching(false)
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
            policy.HasMany(p => p.Notes).WithOne().HasForeignKey(n => n.PolicyId);
            policy.IsTemporal();

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
