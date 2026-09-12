using System.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.IntegrationTests;

/// <summary>
/// An ambient <see cref="TransactionScope"/> the caller opens around a <c>SaveChanges</c> that has no
/// transaction of its own enlists the underlying Npgsql connection automatically; opening a second,
/// EF-managed transaction on top of that is exactly what EF Core refuses
/// (<c>InvalidOperationException: An ambient transaction has been detected...</c>), which both writers
/// used to do unconditionally whenever <c>context.Database.CurrentTransaction</c> was <see langword="null"/>.
/// <see cref="Writers.HistoryWriterTransaction"/> now detects <see cref="Transaction.Current"/> first and,
/// instead of opening its own transaction, opens the connection explicitly so it enlists — every command
/// from then on (the data write, the trigger's change-context push, the history <c>INSERT</c>) rides that
/// enlistment with no separate <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/>
/// to manage, and commit/rollback is entirely the ambient scope's job. These tests confirm that end to
/// end — not just "no exception", but that <c>scope.Complete()</c> and an incomplete scope actually
/// commit/roll back the data change and the history row(s) together, in both writer modes and regardless
/// of whether the connection was touched before <c>SaveChanges</c> is the first operation to run on it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AmbientTransactionScopeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // PostgreSQL truncates identifiers at 63 bytes; two [Theory] cases whose full method name is
    // truncated to the same prefix would collide (CLAUDE.md rule: integration tests never share a
    // database).
    private static string DbName(string label, HistoryWriter writer)
        => (label.Length > 58 ? label[..58] : label) + (writer == HistoryWriter.Trigger ? "_trg" : "_int");

    private static async Task<WidgetContext> CreateContextAsync(
        PostgresFixture postgres, string label, HistoryWriter writer, CancellationToken ct)
    {
        var cs = await postgres.CreateDatabaseAsync(DbName(label, writer), ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs)
            .UseHindsight(h => h.UseHistoryWriter(writer).WithChangeContext<StubChangeContextProvider>())
            .Options;
        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(ct);
        }

        return new WidgetContext(options);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_scope_as_first_operation_on_the_connection_commits_data_and_history(
        HistoryWriter writer)
    {
        await using var db = await CreateContextAsync(
            postgres, nameof(Ambient_scope_as_first_operation_on_the_connection_commits_data_and_history), writer, Ct);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            // SaveChanges is the very first thing this context does: the connection has never been
            // opened before, so Npgsql enlists it in the ambient transaction the moment it opens.
            db.Widgets.Add(new Widget { Name = "first-op" });
            await db.SaveChangesAsync(Ct);
            scope.Complete();
        }

        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(1, await db.Widgets.CountAsync(Ct));

        var versions = await db.History<Widget>().ToListAsync(Ct);
        var version = Assert.Single(versions);
        Assert.Equal("first-op", version.Entity.Name);
        Assert.Equal(VersionOperation.Insert, version.Operation);
        Assert.True(version.IsCurrent);
        Assert.Equal("u1", version.ChangedBy);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_scope_not_completed_rolls_back_both_data_and_history(HistoryWriter writer)
    {
        await using var db = await CreateContextAsync(
            postgres, nameof(Ambient_scope_not_completed_rolls_back_both_data_and_history), writer, Ct);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            db.Widgets.Add(new Widget { Name = "never-completed" });
            await db.SaveChangesAsync(Ct);

            // Deliberately not calling scope.Complete(): Dispose() below aborts the ambient
            // transaction, which must roll back the data write AND the history write together, since
            // both ran on the same enlisted connection with no transaction of Hindsight's own to
            // separately commit or roll back.
        }

        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(0, await db.Widgets.CountAsync(Ct));
        Assert.Equal(0, await db.History<Widget>().CountAsync(Ct));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_scope_after_another_command_already_ran_in_the_same_scope_commits(
        HistoryWriter writer)
    {
        await using var db = await CreateContextAsync(
            postgres,
            nameof(Ambient_scope_after_another_command_already_ran_in_the_same_scope_commits),
            writer,
            Ct);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            // A query runs first, opening (and Npgsql-pooled-closing) the connection under the ambient
            // transaction before SaveChanges ever touches it — enlistment is already established by
            // the time SaveChanges reopens the (possibly pooled) connection.
            _ = await db.Widgets.CountAsync(Ct);

            db.Widgets.Add(new Widget { Name = "touched-in-scope" });
            await db.SaveChangesAsync(Ct);
            scope.Complete();
        }

        Assert.Equal(1, await db.Widgets.CountAsync(Ct));
        var version = Assert.Single(await db.History<Widget>().ToListAsync(Ct));
        Assert.Equal("touched-in-scope", version.Entity.Name);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_scope_after_the_connection_was_used_before_the_scope_started_commits(
        HistoryWriter writer)
    {
        await using var db = await CreateContextAsync(
            postgres,
            nameof(Ambient_scope_after_the_connection_was_used_before_the_scope_started_commits),
            writer,
            Ct);

        // Touch the connection well before any ambient transaction exists.
        _ = await db.Widgets.CountAsync(Ct);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            db.Widgets.Add(new Widget { Name = "touched-before-scope" });
            await db.SaveChangesAsync(Ct);
            scope.Complete();
        }

        Assert.Equal(1, await db.Widgets.CountAsync(Ct));
        var version = Assert.Single(await db.History<Widget>().ToListAsync(Ct));
        Assert.Equal("touched-before-scope", version.Entity.Name);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ambient_scope_rollback_leaves_a_later_plain_SaveChanges_on_the_same_context_working(
        HistoryWriter writer)
    {
        // Confirms the connection Hindsight opened explicitly to force enlistment (DatabaseFacade
        // .OpenConnection) is closed again afterwards rather than left open forever: a later,
        // non-ambient SaveChanges on the very same context still opens its own transaction normally.
        await using var db = await CreateContextAsync(
            postgres,
            nameof(Ambient_scope_rollback_leaves_a_later_plain_SaveChanges_on_the_same_context_working),
            writer,
            Ct);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            db.Widgets.Add(new Widget { Name = "rolled-back" });
            await db.SaveChangesAsync(Ct);
        }

        db.Widgets.Add(new Widget { Name = "plain-save" });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await db.Widgets.CountAsync(Ct));
        var version = Assert.Single(await db.History<Widget>().ToListAsync(Ct));
        Assert.Equal("plain-save", version.Entity.Name);
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
