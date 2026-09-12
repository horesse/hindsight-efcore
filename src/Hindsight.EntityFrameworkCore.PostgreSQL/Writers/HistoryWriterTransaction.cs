using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// Lines up the transaction that keeps the data change and the history row(s) atomic when the caller
/// has not already opened one (DESIGN.md D3), shared by <see cref="HistoryWriterInterceptor"/> and
/// <see cref="HistoryTriggerContextInterceptor"/>. Three cases, checked in order:
/// <c>context.Database.CurrentTransaction</c> already set (the caller opened one — use it, nothing to
/// do here); an ambient <see cref="System.Transactions.Transaction.Current"/> — Npgsql auto-enlists
/// the connection in it as soon as the connection opens, so every command run on that connection from
/// then on (the trigger's context push, EF Core's own data write, Hindsight's history <c>INSERT</c>)
/// commits or rolls back with it whether or not it carries an explicit <see cref="System.Data.Common.DbTransaction"/> —
/// opening a second, EF-managed transaction on top is exactly what EF Core refuses
/// (<c>InvalidOperationException: An ambient transaction has been detected...</c>), so instead the
/// connection is opened explicitly (<c>context.Database.OpenConnection()</c>) to force the enlistment
/// now, before returning, and the caller is expected to close it again with
/// <see cref="Outcome.CloseAmbientConnection"/> / <see cref="Outcome.CloseAmbientConnectionAsync"/> once
/// it is done; otherwise (no transaction of any kind) a new EF transaction is opened and owned by the
/// caller. A retrying execution strategy (<c>EnableRetryOnFailure()</c>) can re-run the whole
/// <c>SaveChanges</c> call after a transient failure; a transaction opened here on the caller's behalf
/// would not survive that retry, so this throws first with a message that names Hindsight and the fix,
/// instead of letting EF Core's own (unrelated) refusal to retry inside an ambient transaction, or its
/// refusal to nest a second explicit transaction, surface instead. That check only applies to the
/// "open a new EF transaction" case: the ambient-transaction case opens no transaction of its own on the
/// caller's behalf, so whether retry and an ambient <c>TransactionScope</c> can be combined at all is
/// between the caller and EF Core's own execution strategy, not something Hindsight decides.
/// </summary>
internal static class HistoryWriterTransaction
{
    public static Outcome BeginIfNeeded(DbContext context, HistoryWriter writer)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return default;
        }

        if (System.Transactions.Transaction.Current is not null)
        {
            context.Database.OpenConnection();
            return Outcome.AmbientTransaction;
        }

        ThrowIfRetryStrategyConfigured(context, writer);
        return new Outcome(context.Database.BeginTransaction());
    }

    public static async Task<Outcome> BeginIfNeededAsync(
        DbContext context, HistoryWriter writer, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return default;
        }

        if (System.Transactions.Transaction.Current is not null)
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            return Outcome.AmbientTransaction;
        }

        ThrowIfRetryStrategyConfigured(context, writer);
        return new Outcome(await context.Database.BeginTransactionAsync(cancellationToken));
    }

    private static void ThrowIfRetryStrategyConfigured(DbContext context, HistoryWriter writer)
    {
        if (!context.Database.CreateExecutionStrategy().RetriesOnFailure)
        {
            return;
        }

        throw new InvalidOperationException(
            "SaveChanges was called with no ambient transaction, but this context is configured with "
            + "both a retrying execution strategy (e.g. EnableRetryOnFailure()) and Hindsight's "
            + $"HistoryWriter.{writer} writer, which needs the data change and the history row(s) to "
            + "commit in one transaction. A transaction opened here, inside SaveChanges, would not "
            + "survive the execution strategy retrying the whole call after a transient failure, so EF "
            + "Core refuses it. Wrap the call instead: await "
            + "context.Database.CreateExecutionStrategy().ExecuteAsync(async () => { await using "
            + "var tx = await context.Database.BeginTransactionAsync(); /* ...SaveChangesAsync()... */ "
            + "await tx.CommitAsync(); }); — Hindsight will use that transaction instead of opening its "
            + "own. See docs/articles/configuration.md.");
    }

    /// <summary>
    /// What <see cref="BeginIfNeeded"/> / <see cref="BeginIfNeededAsync"/> did. <see cref="OwnedTransaction"/>
    /// is set only when a brand-new EF transaction was opened and must be committed/rolled back and
    /// disposed by the caller; it is <see langword="null"/> both when the caller already had a
    /// transaction open and when an ambient <see cref="System.Transactions.Transaction.Current"/> was
    /// found instead — in the latter case <see cref="OpenedConnectionForAmbientTransaction"/> is
    /// <see langword="true"/> and the caller must close the connection again (once, matching the
    /// explicit open) with <see cref="CloseAmbientConnection"/> / <see cref="CloseAmbientConnectionAsync"/>
    /// after it is done, whether the save succeeded or failed — the ambient transaction itself commits or
    /// rolls back independently, driven by the caller's own <see cref="System.Transactions.TransactionScope"/>.
    /// </summary>
    public readonly struct Outcome(IDbContextTransaction? ownedTransaction, bool openedConnectionForAmbientTransaction)
    {
        public static readonly Outcome AmbientTransaction = new(null, true);

        public Outcome(IDbContextTransaction? ownedTransaction)
            : this(ownedTransaction, false)
        {
        }

        public IDbContextTransaction? OwnedTransaction { get; } = ownedTransaction;

        public bool OpenedConnectionForAmbientTransaction { get; } = openedConnectionForAmbientTransaction;

        public void CloseAmbientConnection(DbContext context)
        {
            if (OpenedConnectionForAmbientTransaction)
            {
                context.Database.CloseConnection();
            }
        }

        public async Task CloseAmbientConnectionAsync(DbContext context)
        {
            if (OpenedConnectionForAmbientTransaction)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }
}
