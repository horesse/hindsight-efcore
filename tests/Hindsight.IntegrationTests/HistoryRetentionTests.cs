using Hindsight.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for history retention on a real PostgreSQL (DESIGN.md D19):
/// <see cref="HindsightDbContextExtensions.PruneHistoryAsync{TEntity}"/>, the retention horizon it records,
/// and the <c>hindsight_history_retained</c> guard that makes <c>AsOf</c> / <c>FromTo</c> /
/// <c>ContainedIn</c> throw before the horizon instead of answering from pruned history.
/// Every scenario that writes history runs under both writers. The timeline is the same for both:
/// six saves, one per step — under the Interceptor writer an hour apart through <see cref="TimeProvider"/>,
/// under the Trigger writer in separate transactions, so <c>now()</c> differs — and every instant a test
/// uses is read back from the history rather than assumed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryRetentionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- pruning keeps AsOf exact from the cutoff on --------------------------------------------------

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Prune_keeps_AsOf_exact_at_and_after_the_cutoff(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "prune_keeps_asof_exact");
        var probes = Probes(h.Timeline, from: h.Timeline.PolicyTwoDeleted);
        var before = await AsOfAllAsync(h.Db, probes);

        var deleted = await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        // Policy 1's first version [e1, e2), policy 2's only version [e1, e3) and its tombstone [e3, e3).
        Assert.Equal(3, deleted);
        Assert.Equal(before, await AsOfAllAsync(h.Db, probes));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Prune_never_deletes_a_current_version_however_old(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "prune_keeps_current");

        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.End, cancellationToken: Ct);

        var versions = await h.Db.History<Policy>().ToListAsync(Ct);
        Assert.All(versions, v => Assert.True(v.IsCurrent));
        Assert.Equal([1, 3, 4], versions.Select(v => v.Entity.Id).Order());

        // Policy 3 never changed after its insert at the very start: its only version is kept.
        Assert.Equal(h.Timeline.Start, versions.Single(v => v.Entity.Id == 3).ValidFrom);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task An_entity_deleted_before_the_cutoff_leaves_no_history(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "prune_removes_deleted_entity");

        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        Assert.DoesNotContain(await h.Db.History<Policy>().ToListAsync(Ct), v => v.Entity.Id == 2);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task AllVersions_after_pruning_starts_at_the_first_retained_version(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "prune_allversions_retained");

        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        // Policy 1 was Draft [e1, e2), Active [e2, e4), Cancelled [e4, ∞); Draft ended before the cutoff.
        var policyOne = await h.Db.History<Policy>().Where(v => v.Entity.Id == 1).ToListAsync(Ct);
        Assert.Equal(["Cancelled", "Active"], policyOne.Select(v => v.Entity.Status));
        Assert.Equal(VersionOperation.Update, policyOne[^1].Operation);
        Assert.Equal(policyOne[1].ValidTo, policyOne[0].ValidFrom);

        Assert.Equal(
            ["Cancelled", "Active"],
            await h.Db.Policies.AllVersions().Where(p => p.Id == 1).Select(p => p.Status).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Writes_after_pruning_continue_the_retained_history(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "prune_then_write");
        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.End, cancellationToken: Ct);

        await h.StepAsync(async db => (await db.Policies.SingleAsync(p => p.Id == 1, Ct)).Status = "Reinstated");
        await h.StepAsync(async db => db.Policies.Remove(await db.Policies.SingleAsync(p => p.Id == 3, Ct)));

        var one = await h.Db.History<Policy>().Where(v => v.Entity.Id == 1).ToListAsync(Ct);
        Assert.Equal(["Reinstated", "Cancelled"], one.Select(v => v.Entity.Status));
        Assert.Equal(one[1].ValidTo, one[0].ValidFrom);
        Assert.True(one[0].IsCurrent);

        var three = await h.Db.History<Policy>().Where(v => v.Entity.Id == 3).ToListAsync(Ct);
        Assert.Equal([VersionOperation.Delete, VersionOperation.Insert], three.Select(v => v.Operation));
        Assert.DoesNotContain(three, v => v.IsCurrent);
    }

    // --- the horizon ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task AsOf_before_the_horizon_throws_even_when_no_row_would_match(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "asof_before_horizon_throws");
        var horizon = h.Timeline.PolicyTwoDeleted;
        await h.Db.PruneHistoryAsync<Policy>(horizon, cancellationToken: Ct);
        var justBefore = horizon.AddTicks(-10); // 1 µs: PostgreSQL's resolution

        var ex = await AssertHorizonViolationAsync(() =>
            h.Db.Policies.AsOf(justBefore).Where(p => p.Id == -1).ToListAsync(Ct));
        Assert.Contains("Policy#History", ex.MessageText, StringComparison.Ordinal);

        await AssertHorizonViolationAsync(() => h.Db.Policies.AsOf(h.Timeline.Start).CountAsync(Ct));
        await AssertHorizonViolationAsync(() => h.Db.Policies.AsOf(justBefore).AnyAsync(Ct));

        // At the horizon itself the history is complete.
        Assert.Equal([1, 3], await h.Db.Policies.AsOf(horizon).Select(p => p.Id).OrderBy(id => id).ToListAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task FromTo_and_ContainedIn_starting_before_the_horizon_throw(HistoryWriter writer)
    {
        await using var h = await SeedTimelineAsync(writer, "range_before_horizon_throws");
        var horizon = h.Timeline.PolicyTwoDeleted;
        await h.Db.PruneHistoryAsync<Policy>(horizon, cancellationToken: Ct);

        await AssertHorizonViolationAsync(() =>
            h.Db.Policies.FromTo(h.Timeline.Start, DateTimeOffset.MaxValue).ToListAsync(Ct));
        await AssertHorizonViolationAsync(() =>
            h.Db.Policies.ContainedIn(h.Timeline.Start, DateTimeOffset.MaxValue).ToListAsync(Ct));

        // From the horizon on: policy 1 Active [e2, e4) and Cancelled, policy 3, policy 4's two versions
        // overlap the window; only the three that started at or after it are contained in it.
        Assert.Equal(5, await h.Db.Policies.FromTo(horizon, DateTimeOffset.MaxValue).CountAsync(Ct));
        Assert.Equal(3, await h.Db.Policies.ContainedIn(horizon, DateTimeOffset.MaxValue).CountAsync(Ct));
    }

    [Fact]
    public async Task A_query_combining_an_instant_before_and_after_the_horizon_throws()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "concat_before_horizon_throws");
        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        var query = h.Db.Policies.AsOf(h.Timeline.End).Select(p => p.Id)
            .Concat(h.Db.Policies.AsOf(h.Timeline.Start).Select(p => p.Id));

        await AssertHorizonViolationAsync(() => query.ToListAsync(Ct));
    }

    [Fact]
    public async Task History_that_was_never_pruned_answers_every_instant_and_has_no_horizon()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "never_pruned_no_horizon");

        Assert.Empty(await h.Db.Policies.AsOf(h.Timeline.Start.AddYears(-1)).ToListAsync(Ct));
        Assert.Null(await h.Db.GetHistoryHorizonAsync<Policy>(Ct));
    }

    [Fact]
    public async Task GetHistoryHorizonAsync_returns_the_recorded_cutoff()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "horizon_is_recorded");

        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        Assert.Equal(h.Timeline.PolicyTwoDeleted, await h.Db.GetHistoryHorizonAsync<Policy>(Ct));
    }

    [Fact]
    public async Task Pruning_with_an_earlier_cutoff_never_moves_the_horizon_back()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "horizon_never_moves_back");
        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.Start, cancellationToken: Ct);

        Assert.Equal(h.Timeline.PolicyTwoDeleted, await h.Db.GetHistoryHorizonAsync<Policy>(Ct));
        await AssertHorizonViolationAsync(() =>
            h.Db.Policies.AsOf(h.Timeline.PolicyTwoDeleted.AddTicks(-10)).ToListAsync(Ct));
    }

    [Fact]
    public async Task Pruning_one_entity_leaves_another_entitys_history_and_queries_alone()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "prune_is_per_entity");
        h.Db.Readings.Add(new Reading { Id = 1, Value = 1 });
        await h.Db.SaveChangesAsync(Ct);

        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);

        Assert.Null(await h.Db.GetHistoryHorizonAsync<Reading>(Ct));
        Assert.Empty(await h.Db.Readings.AsOf(h.Timeline.Start).ToListAsync(Ct));
    }

    // --- arguments and configuration -----------------------------------------------------------------

    [Fact]
    public async Task Prune_rejects_a_cutoff_later_than_the_database_clock_and_changes_nothing()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "prune_rejects_future_cutoff");
        var rowsBefore = await h.Db.History<Policy>().CountAsync(Ct);

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.Db.PruneHistoryAsync<Policy>(DateTimeOffset.UtcNow.AddDays(1), cancellationToken: Ct));

        Assert.Equal("olderThan", ex.ParamName);
        Assert.Equal(rowsBefore, await h.Db.History<Policy>().CountAsync(Ct));
        Assert.Null(await h.Db.GetHistoryHorizonAsync<Policy>(Ct));
    }

    [Fact]
    public async Task Prune_rejects_MaxValue_and_a_non_positive_batch_size()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "prune_rejects_bad_arguments");

        var maxValue = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.Db.PruneHistoryAsync<Policy>(DateTimeOffset.MaxValue, cancellationToken: Ct));
        var batch = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.Db.PruneHistoryAsync<Policy>(h.Timeline.Start, batchSize: 0, cancellationToken: Ct));

        Assert.Equal("olderThan", maxValue.ParamName);
        Assert.Equal("batchSize", batch.ParamName);
    }

    [Fact]
    public async Task Prune_requires_WithRetention_and_a_temporal_entity()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "prune_requires_with_retention");

        var noRetention = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.PruneHistoryAsync<Reading>(h.Timeline.Start, cancellationToken: Ct));
        var notTemporal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Db.PruneHistoryAsync<Widget>(h.Timeline.Start, cancellationToken: Ct));

        Assert.Contains("WithRetention()", noRetention.Message, StringComparison.Ordinal);
        Assert.Contains("Reading", noRetention.Message, StringComparison.Ordinal);
        Assert.Contains("temporal", notTemporal.Message, StringComparison.Ordinal);
    }

    // --- batching and transactions -------------------------------------------------------------------

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Prune_deletes_in_batches_until_nothing_is_left(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, "prune_batches");
        await h.StepAsync(db => db.Policies.Add(new Policy { Id = 1, Status = "v0" }));
        for (var i = 1; i <= 7; i++)
        {
            var status = "v" + i;
            await h.StepAsync(async db => (await db.Policies.SingleAsync(Ct)).Status = status);
        }

        var current = await h.Db.History<Policy>().Where(v => v.Entity.Status == "v7").SingleAsync(Ct);

        var deleted = await h.Db.PruneHistoryAsync<Policy>(current.ValidFrom, batchSize: 2, cancellationToken: Ct);

        Assert.Equal(7, deleted);
        Assert.Equal(["v7"], await h.Db.History<Policy>().Select(v => v.Entity.Status).ToListAsync(Ct));
    }

    [Fact]
    public async Task Prune_inside_a_caller_transaction_rolls_back_with_it()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "prune_in_caller_transaction");
        var rowsBefore = await h.Db.History<Policy>().CountAsync(Ct);

        await using (var tx = await h.Db.Database.BeginTransactionAsync(Ct))
        {
            Assert.Equal(3, await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct));
            await tx.RollbackAsync(Ct);
        }

        Assert.Equal(rowsBefore, await h.Db.History<Policy>().CountAsync(Ct));
        Assert.Null(await h.Db.GetHistoryHorizonAsync<Policy>(Ct));
    }

    [Fact]
    public async Task Prune_statement_can_be_served_by_the_period_range_gist_index()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "prune_uses_gist");
        var sql = RetentionSqlGenerator.DeleteBatch(
            "policies_history", null, "valid_from", "valid_to", h.Db.GetService<ISqlGenerationHelper>());

        // With a handful of rows the planner rightly prefers a sequential scan; switching it off shows the
        // index is applicable to the predicate, which is what the statement is responsible for.
        await h.Db.Database.OpenConnectionAsync(Ct);
        await using var tx = await h.Db.Database.BeginTransactionAsync(Ct);
        await h.Db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", Ct);

        await using var command = h.Db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = "EXPLAIN " + sql.Replace("{0}", "@cutoff", StringComparison.Ordinal).Replace("{1}", "@batch", StringComparison.Ordinal);
        command.Parameters.Add(new NpgsqlParameter("cutoff", h.Timeline.PolicyTwoDeleted.UtcDateTime));
        command.Parameters.Add(new NpgsqlParameter("batch", 10));

        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        Assert.Contains("ix_policies_history_period", string.Join('\n', plan), StringComparison.Ordinal);
    }

    // --- generated DDL and SQL ------------------------------------------------------------------------

    [Fact]
    public async Task Retention_ddl_has_the_expected_shape()
    {
        await using var db = new PolicyContext(new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .EnableServiceProviderCaching(false)
            .UseHindsight()
            .Options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());

        var sql = db.GetService<IMigrationsSqlGenerator>().Generate(operations, model)
            .Select(command => command.CommandText)
            .Where(text => text.Contains(RetentionSqlGenerator.TableName, StringComparison.Ordinal));

        await Verify(string.Join("\n\n", sql), extension: "sql");
    }

    [Fact]
    public async Task Applied_schema_has_the_horizon_table_and_the_guard_function()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, "applied_schema");
        await using var conn = new NpgsqlConnection(h.Db.Database.GetConnectionString());
        await conn.OpenAsync(Ct);

        await using var columns = conn.CreateCommand();
        columns.CommandText =
            """
            SELECT string_agg(column_name || ' ' || data_type || ' ' || is_nullable, ', ' ORDER BY ordinal_position)
            FROM information_schema.columns WHERE table_name = 'hindsight_retention_horizon'
            """;
        Assert.Equal(
            "history_entity text NO, horizon timestamp with time zone NO",
            await columns.ExecuteScalarAsync(Ct));

        await using var function = conn.CreateCommand();
        function.CommandText =
            """
            SELECT provolatile::text || proparallel::text FROM pg_proc WHERE proname = 'hindsight_history_retained'
            """;
        Assert.Equal("ss", await function.ExecuteScalarAsync(Ct)); // STABLE, PARALLEL SAFE
    }

    [Fact]
    public async Task Moving_to_another_default_schema_keeps_the_horizon_and_the_guard_working()
    {
        await using var h = await SeedTimelineAsync(HistoryWriter.Interceptor, "horizon_schema_move");
        await h.Db.PruneHistoryAsync<Policy>(h.Timeline.PolicyTwoDeleted, cancellationToken: Ct);
        var cs = h.Db.Database.GetConnectionString()!;

        await using var moved = new AuditSchemaPolicyContext(new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseHindsight()
            .Options);
        var from = h.Db.GetService<IDesignTimeModel>().Model;
        var to = moved.GetService<IDesignTimeModel>().Model;
        var operations = moved.GetService<IMigrationsModelDiffer>().GetDifferences(from.GetRelationalModel(), to.GetRelationalModel());
        await moved.GetService<IMigrationCommandExecutor>().ExecuteNonQueryAsync(
            moved.GetService<IMigrationsSqlGenerator>().Generate(operations, to),
            moved.GetService<IRelationalConnection>(),
            Ct);

        Assert.Equal(h.Timeline.PolicyTwoDeleted, await moved.GetHistoryHorizonAsync<Policy>(Ct));
        await AssertHorizonViolationAsync(() => moved.Policies.AsOf(h.Timeline.Start).ToListAsync(Ct));
        Assert.Equal(2, await moved.Policies.AsOf(h.Timeline.PolicyTwoDeleted).CountAsync(Ct));
    }

    [Fact]
    public async Task AsOf_with_retention_generates_the_expected_sql()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, "asof_retention_sql");

        var sql = h.Db.Policies
            .AsOf(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Where(p => p.Status == "Active")
            .ToQueryString();

        await Verify(sql, extension: "sql");
    }

    // ---------------------------------------------------------------------------------------------

    // The guard's error, whether it surfaces when the command starts or while its rows are read.
    private static async Task<PostgresException> AssertHorizonViolationAsync(Func<Task> query)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(query);
        Assert.Equal("HS001", ex.SqlState);
        Assert.Contains("retention horizon", ex.MessageText, StringComparison.Ordinal);
        return ex;
    }

    // Instants at and after `from`: every period boundary of the timeline, a microsecond either side of
    // it, and the far future.
    private static List<DateTimeOffset> Probes(Timeline timeline, DateTimeOffset from)
        => timeline.Boundaries
            .SelectMany(b => new[] { b.AddTicks(-10), b, b.AddTicks(10) })
            .Append(timeline.End.AddYears(10))
            .Where(t => t >= from)
            .Distinct()
            .Order()
            .ToList();

    private static async Task<List<string>> AsOfAllAsync(PolicyContext db, List<DateTimeOffset> instants)
    {
        var answers = new List<string>();
        foreach (var t in instants)
        {
            var rows = await db.Policies.AsOf(t).OrderBy(p => p.Id).Select(p => p.Id + ":" + p.Status).ToListAsync(Ct);
            answers.Add($"{t:O} => {string.Join(", ", rows)}");
        }

        return answers;
    }

    // e1: insert policies 1, 2, 3 · e2: update 1 · e3: delete 2 · e4: update 1 · e5: insert 4 · e6: update 4.
    private async Task<Harness> SeedTimelineAsync(HistoryWriter writer, string dbName)
    {
        var h = await CreateAsync(writer, dbName);

        await h.StepAsync(db =>
        {
            db.Policies.Add(new Policy { Id = 1, Status = "Draft" });
            db.Policies.Add(new Policy { Id = 2, Status = "Draft" });
            db.Policies.Add(new Policy { Id = 3, Status = "Draft" });
        });
        await h.StepAsync(async db => (await db.Policies.SingleAsync(p => p.Id == 1, Ct)).Status = "Active");
        await h.StepAsync(async db => db.Policies.Remove(await db.Policies.SingleAsync(p => p.Id == 2, Ct)));
        await h.StepAsync(async db => (await db.Policies.SingleAsync(p => p.Id == 1, Ct)).Status = "Cancelled");
        await h.StepAsync(db => db.Policies.Add(new Policy { Id = 4, Status = "Draft" }));
        await h.StepAsync(async db => (await db.Policies.SingleAsync(p => p.Id == 4, Ct)).Status = "Active");

        var rows = await h.Db.History<Policy>().ToListAsync(Ct);
        h.Timeline = new Timeline(
            Start: rows.Min(v => v.ValidFrom),
            PolicyTwoDeleted: rows.Single(v => v.Operation == VersionOperation.Delete).ValidFrom,
            End: rows.Max(v => v.ValidFrom),
            Boundaries: [.. rows.SelectMany(v => new[] { v.ValidFrom, v.ValidTo }).Where(t => t != DateTimeOffset.MaxValue).Distinct()]);
        return h;
    }

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var cs = await postgres.CreateDatabaseAsync("retention_" + dbName + suffix, Ct);

        // Interceptor timestamps come from this clock; start a month back so every cutoff the tests
        // derive from the timeline is before the database's now().
        var start = DateTimeOffset.UtcNow.AddDays(-30);
        var time = new MutableTimeProvider(new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero));

        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseApplicationServiceProvider(new SingleServiceProvider(typeof(TimeProvider), time))
            .UseHindsight(hb => hb.UseHistoryWriter(writer))
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(db, time);
    }

    private sealed record Timeline(
        DateTimeOffset Start,
        DateTimeOffset PolicyTwoDeleted,
        DateTimeOffset End,
        List<DateTimeOffset> Boundaries);

    private sealed class Harness(PolicyContext db, MutableTimeProvider time) : IAsyncDisposable
    {
        public PolicyContext Db { get; } = db;

        public Timeline Timeline { get; set; } = null!;

        // One save per step: an hour later on the injected clock (Interceptor), a new transaction and
        // so a later now() (Trigger).
        public async Task StepAsync(Func<PolicyContext, Task> change)
        {
            time.Advance(TimeSpan.FromHours(1));
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

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class Reading
    {
        public int Id { get; set; }
        public double Value { get; set; }
    }

    private sealed class Widget
    {
        public int Id { get; set; }
    }

    // The same model with every table in schema "audit": its own CLR type, so its own cached model.
    private sealed class AuditSchemaPolicyContext(DbContextOptions<PolicyContext> options) : PolicyContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("audit");
            base.OnModelCreating(modelBuilder);
        }
    }

    private class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        public DbSet<Reading> Readings => Set<Reading>();

        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(p => p.Status).HasColumnName("status");
            policy.IsTemporal(t => t.WithRetention());

            var reading = modelBuilder.Entity<Reading>();
            reading.ToTable("readings");
            reading.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            reading.Property(r => r.Value).HasColumnName("value");
            reading.IsTemporal();

            var widget = modelBuilder.Entity<Widget>();
            widget.ToTable("widgets");
            widget.Property(w => w.Id).HasColumnName("id");
        }
    }
}
