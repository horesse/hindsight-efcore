using System.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.IntegrationTests;

/// <summary>
/// <c>UseNpgsql(cs, o =&gt; o.EnableRetryOnFailure())</c> installs a retrying execution strategy, which
/// forbids a transaction from being opened anywhere it would not survive the strategy retrying the
/// whole <c>SaveChanges</c> call after a transient failure. Both history writers open a transaction
/// themselves when the caller has none (DESIGN.md D3) — these tests pin down exactly when that
/// collides with a retrying strategy, that Hindsight now reports it with its own message instead of
/// forwarding whichever of EF Core's or Npgsql's exceptions happens to surface, and that the
/// documented <c>CreateExecutionStrategy().Execute(...)</c> workaround (with the caller opening its
/// own transaction) is unaffected.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetryingExecutionStrategyTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // PostgreSQL truncates identifiers at 63 bytes; a full test-method name plus a HistoryWriter suffix
    // easily exceeds that; two [Theory] cases truncated to the same prefix would collide (CLAUDE.md rule:
    // integration tests never share a database).
    private static string DbName(string label, HistoryWriter? writer = null)
    {
        var suffix = writer switch
        {
            HistoryWriter.Trigger => "_trg",
            HistoryWriter.Interceptor => "_int",
            _ => "",
        };
        var trimmed = label.Length > 58 ? label[..58] : label;
        return trimmed + suffix;
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Bare_SaveChanges_with_retry_enabled_and_no_caller_transaction_throws_a_clear_error(
        HistoryWriter writer)
    {
        // A change context provider is configured so the Trigger writer also has something to push:
        // without one, Capture() short-circuits before ever touching a transaction (it has nothing to
        // record), so the retry conflict would not be exercised for that writer.
        var cs = await postgres.CreateDatabaseAsync(DbName("bare_savechanges_retry", writer), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .UseHindsight(h => h.UseHistoryWriter(writer).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);
        db.Widgets.Add(new Widget { Name = "a" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        Assert.Contains("retrying execution strategy", ex.Message);
        Assert.Contains($"HistoryWriter.{writer}", ex.Message);
        Assert.Contains("CreateExecutionStrategy", ex.Message);
    }

    [Fact]
    public async Task Trigger_with_no_change_context_configured_never_touches_a_transaction_and_succeeds()
    {
        // The one case where the retrying strategy does not matter at all: nothing (no provider, no
        // ChangeReasonScope) means the Trigger writer has no context to push, so it never calls
        // BeginTransaction and the conflict never arises.
        var cs = await postgres.CreateDatabaseAsync(DbName("trigger_no_context_retry"), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger))
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);
        db.Widgets.Add(new Widget { Name = "a" });

        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await db.Widgets.CountAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task SaveChanges_wrapped_in_CreateExecutionStrategy_with_the_callers_own_transaction_succeeds(
        HistoryWriter writer)
    {
        // The documented EF Core pattern for combining retry with an ambient transaction. Because the
        // caller's BeginTransactionAsync runs before SavingChanges fires, CurrentTransaction is already
        // non-null there, so neither writer attempts to open its own — no conflict, no double
        // transaction, no retry-strategy rejection.
        var cs = await postgres.CreateDatabaseAsync(DbName("wrapped_execution_strategy", writer), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .UseHindsight(h => h.UseHistoryWriter(writer).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.Widgets.Add(new Widget { Name = "b" });
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        });

        Assert.Equal(1, await db.Widgets.CountAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_TransactionScope_with_retry_enabled_throws(HistoryWriter writer)
    {
        // Independent known EF Core caveat (retry + ambient TransactionScope) plus, once the writer
        // would actually touch a transaction, Hindsight's own guard: either way this must throw, never
        // silently write history outside the caller's ambient transaction.
        var cs = await postgres.CreateDatabaseAsync(DbName("ambient_txscope_retry", writer), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .UseHindsight(h => h.UseHistoryWriter(writer).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            db.Widgets.Add(new Widget { Name = "c" });
            await db.SaveChangesAsync(Ct);
            scope.Complete();
        });
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_TransactionScope_without_retry_also_throws_a_preexisting_and_unrelated_error(
        HistoryWriter writer)
    {
        // Control for the scenario above: this fails identically with EnableRetryOnFailure absent, so
        // the ambient-TransactionScope incompatibility is a pre-existing limitation of "the writer
        // opens its own transaction" (DESIGN.md D3) and not something the retry-strategy fix introduces
        // or needs to solve — see docs/articles/limitations.md.
        var cs = await postgres.CreateDatabaseAsync(DbName("ambient_txscope_no_retry", writer), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs) // no EnableRetryOnFailure
            .UseHindsight(h => h.UseHistoryWriter(writer).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            db.Widgets.Add(new Widget { Name = "d" });
            await db.SaveChangesAsync(Ct);
            scope.Complete();
        });

        Assert.Contains("ambient transaction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class StubChangeContextProvider : IChangeContextProvider
    {
        public ChangeContext GetChangeContext(DbContext context) => ChangeContext.Empty with { UserId = "u1" };
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var widget = modelBuilder.Entity<Widget>();
            widget.ToTable("widgets");
            widget.IsTemporal();
        }
    }
}
