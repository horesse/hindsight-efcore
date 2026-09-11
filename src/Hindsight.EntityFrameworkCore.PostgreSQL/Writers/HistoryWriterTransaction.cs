using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// Opens the transaction that keeps the data change and the history row(s) atomic when the caller has
/// not already opened one (DESIGN.md D3), shared by <see cref="HistoryWriterInterceptor"/> and
/// <see cref="HistoryTriggerContextInterceptor"/>. A retrying execution strategy
/// (<c>EnableRetryOnFailure()</c>) can re-run the whole <c>SaveChanges</c> call after a transient
/// failure; a transaction opened here on the caller's behalf would not survive that retry, so EF Core
/// (or Npgsql, for an ambient <see cref="System.Transactions.TransactionScope"/>) refuses it — with a
/// message that never mentions Hindsight and, between the two writers, is not even the same exception.
/// Detect the same condition first with the public <see cref="IExecutionStrategy.RetriesOnFailure"/> and
/// fail with the actual cause and the fix, instead of forwarding whichever exception happens to surface.
/// </summary>
internal static class HistoryWriterTransaction
{
    public static IDbContextTransaction? BeginIfNeeded(DbContext context, HistoryWriter writer)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return null;
        }

        ThrowIfRetryStrategyConfigured(context, writer);
        return context.Database.BeginTransaction();
    }

    public static async Task<IDbContextTransaction?> BeginIfNeededAsync(
        DbContext context, HistoryWriter writer, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return null;
        }

        ThrowIfRetryStrategyConfigured(context, writer);
        return await context.Database.BeginTransactionAsync(cancellationToken);
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
            + "Core (or Npgsql, for an ambient TransactionScope) refuses it. Wrap the call instead: "
            + "await context.Database.CreateExecutionStrategy().ExecuteAsync(async () => { await using "
            + "var tx = await context.Database.BeginTransactionAsync(); /* ...SaveChangesAsync()... */ "
            + "await tx.CommitAsync(); }); — Hindsight will use that transaction instead of opening its "
            + "own. See docs/articles/configuration.md.");
    }
}
