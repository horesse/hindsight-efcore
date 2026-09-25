using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// <c>UseNpgsql(cs, o =&gt; o.EnableRetryOnFailure())</c> installs a retrying execution strategy, which
/// forbids a transaction from being opened anywhere it would not survive the strategy retrying the
/// whole <c>SaveChanges</c> call after a transient failure. The Interceptor writer opens a transaction
/// itself when the caller has none (DESIGN.md D3), so under retry it reports the conflict with its own
/// message; the Trigger writer instead has EF Core open one inside the strategy on every attempt and
/// pushes the change context into each. The documented <c>CreateExecutionStrategy().Execute(...)</c>
/// pattern (the caller opening its own transaction) works with both.
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

    [Fact]
    public async Task Interceptor_bare_SaveChanges_with_retry_enabled_and_no_caller_transaction_throws_a_clear_error()
    {
        const HistoryWriter writer = HistoryWriter.Interceptor;
        var cs = await postgres.CreateDatabaseAsync(DbName("bare_savechanges_retry", writer), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .EnableServiceProviderCaching(false)
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
    public async Task Trigger_bare_SaveChanges_with_retry_enabled_records_the_change_context()
    {
        // No caller transaction: the Trigger writer opens none of its own, but has EF Core open one
        // inside the execution strategy and pushes the context into it.
        var cs = await postgres.CreateDatabaseAsync(DbName("trigger_bare_retry_context"), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);
        db.Widgets.Add(new Widget { Name = "a" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(["u1"], await ChangedByAsync(cs));
        Assert.Equal(AutoTransactionBehavior.WhenNeeded, db.Database.AutoTransactionBehavior);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trigger_SaveChanges_retried_after_a_transient_failure_records_the_change_context_once(bool async)
    {
        // The first attempt's INSERT fails with a transient error: EF Core rolls its transaction back,
        // retries with a new one, and the context has to be pushed into that one too — otherwise the
        // retried write's history row would carry no context.
        var cs = await postgres.CreateDatabaseAsync(DbName($"trigger_retry_transient_{(async ? "a" : "s")}"), Ct);
        var failOnce = new FailFirstInsertInterceptor();
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null))
            .EnableServiceProviderCaching(false)
            .AddInterceptors(failOnce)
            .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);
        db.Widgets.Add(new Widget { Name = "a" });
        failOnce.Armed = true;
        if (async)
        {
            await db.SaveChangesAsync(Ct);
        }
        else
        {
            db.SaveChanges();
        }

        Assert.True(failOnce.Failed);
        Assert.Equal(1, await db.Widgets.CountAsync(Ct));
        Assert.Equal(["u1"], await ChangedByAsync(cs));
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
            .EnableServiceProviderCaching(false)
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
            .EnableServiceProviderCaching(false)
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
        // An independent, EF-Core-native guard, not Hindsight's own: a retrying execution strategy
        // refuses to run at all under an ambient TransactionScope (a transient failure could not be
        // retried inside it), regardless of whether Hindsight ever touches a transaction. Confirmed
        // unaffected by HistoryWriterTransaction now cooperating with an ambient TransactionScope
        // instead of opening its own — see AmbientTransactionScopeTests for the (no-retry) success
        // path and docs/writing/transactions.md.
        var cs = await postgres.CreateDatabaseAsync(DbName("ambient_txscope_retry", writer), Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs, o => o.EnableRetryOnFailure())
            .EnableServiceProviderCaching(false)
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
            db.Widgets.Add(new Widget { Name = "c" });
            await db.SaveChangesAsync(Ct);
            scope.Complete();
        });

        // EF Core's own message, not Hindsight's — confirms this is the execution-strategy guard, not
        // HistoryWriterTransaction.ThrowIfRetryStrategyConfigured (which never runs for an ambient
        // transaction — see its doc comment).
        Assert.Contains("does not support user-initiated transactions", ex.Message);
        Assert.Equal(0, await db.Widgets.CountAsync(Ct));
    }

    private static async Task<List<string?>> ChangedByAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select changed_by from widgets_history order by history_id";
        var values = new List<string?>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return values;
    }

    // Throws a transient NpgsqlException (its IsTransient is true for a TimeoutException inner exception,
    // which is what NpgsqlRetryingExecutionStrategy retries on) in place of the first INSERT into widgets.
    private sealed class FailFirstInsertInterceptor : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public bool Failed { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            FailIfArmed(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            FailIfArmed(command);
            return ValueTask.FromResult(result);
        }

        private void FailIfArmed(DbCommand command)
        {
            if (Armed && !Failed && command.CommandText.Contains("INSERT INTO widgets", StringComparison.Ordinal))
            {
                Failed = true;
                throw new NpgsqlException("simulated transient failure", new TimeoutException());
            }
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class StubChangeContextProvider : IChangeContextProvider
    {
        public ChangeContext GetChangeContext(DbContext context) => ChangeContext.Empty with { ChangedBy = "u1" };
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
