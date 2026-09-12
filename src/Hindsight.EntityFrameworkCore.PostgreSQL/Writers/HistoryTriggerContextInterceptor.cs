using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Hindsight.Infrastructure;
using Hindsight.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// The <see cref="HistoryWriter.Trigger"/> half of the change context (DESIGN.md D3). On
/// <c>SaveChanges</c>, when a temporal entity is being written and there is a
/// <see cref="ChangeContext"/> to record, it pushes the context into the transaction with
/// <c>set_config('hindsight.&lt;key&gt;', value, true)</c> — transaction-local — so the generated
/// trigger can read it back with <c>current_setting('hindsight.&lt;key&gt;', true)</c>.
/// </summary>
/// <remarks>
/// It also opens a transaction when the caller has none, exactly as the Interceptor writer does (via
/// <see cref="HistoryWriterTransaction"/>): EF Core's default <c>AutoTransactionBehavior.WhenNeeded</c>
/// runs a single-statement <c>SaveChanges</c> without a transaction, and <c>set_config(..., true)</c>
/// outside a transaction has no effect. The transaction it opens is committed in <c>SavedChanges</c>
/// and rolled back in <c>SaveChangesFailed</c>, so the data change, the trigger's history rows and the
/// context all commit together. Under an ambient <see cref="System.Transactions.TransactionScope"/>
/// <see cref="HistoryWriterTransaction"/> opens no transaction of its own — it opens the connection
/// explicitly instead, so it enlists in the ambient transaction, and the push command rides that
/// enlistment with no <see cref="System.Data.Common.DbTransaction"/> attached; commit/rollback is then
/// the ambient scope's job, not this interceptor's, and <c>SavedChanges</c>/<c>SaveChangesFailed</c>
/// only close the connection back down again. Bulk operations (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>)
/// do not pass through here: the trigger still records their history, with <see langword="null"/>
/// context columns (DESIGN.md D4). A configured retrying execution strategy makes opening a new EF
/// transaction unsafe — see <see cref="HistoryWriterTransaction"/> — but only once there is actually a
/// context to push (<see cref="Capture"/> returns <see langword="null"/>, and no transaction is touched,
/// when neither a change context provider nor <see cref="ChangeReasonScope"/> is in use).
/// </remarks>
internal sealed class HistoryTriggerContextInterceptor : SaveChangesInterceptor
{
    // Keyed by the context instance, like HistoryWriterInterceptor: at most one live entry per context,
    // removed in the terminal hook. Never a field — the interceptor is shared between contexts.
    private readonly ConditionalWeakTable<DbContext, OwnedTransaction> _pending = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && IsEnabled(context))
        {
            Push(context);
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && IsEnabled(context))
        {
            await PushAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            owned.TransactionOutcome.OwnedTransaction?.Commit();
            owned.TransactionOutcome.OwnedTransaction?.Dispose();
            owned.TransactionOutcome.CloseAmbientConnection(context);
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            if (owned.TransactionOutcome.OwnedTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            await owned.TransactionOutcome.CloseAmbientConnectionAsync(context);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            owned.TransactionOutcome.OwnedTransaction?.Rollback();
            owned.TransactionOutcome.OwnedTransaction?.Dispose();
            owned.TransactionOutcome.CloseAmbientConnection(context);
        }

        base.SaveChangesFailed(eventData);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            if (owned.TransactionOutcome.OwnedTransaction is { } transaction)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            await owned.TransactionOutcome.CloseAmbientConnectionAsync(context);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static bool IsEnabled(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<HindsightOptionsExtension>()?.HistoryWriter
            == HistoryWriter.Trigger;

    private void Push(DbContext context)
    {
        _pending.Remove(context);

        var values = Capture(context);
        if (values is null)
        {
            return;
        }

        var transactionOutcome = HistoryWriterTransaction.BeginIfNeeded(context, HistoryWriter.Trigger);

        try
        {
            var (connection, transaction) = Target(context);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            Bind(command, values);
            command.ExecuteNonQuery();
        }
        catch
        {
            transactionOutcome.OwnedTransaction?.Rollback();
            transactionOutcome.OwnedTransaction?.Dispose();
            transactionOutcome.CloseAmbientConnection(context);
            throw;
        }

        if (transactionOutcome.OwnedTransaction is not null || transactionOutcome.OpenedConnectionForAmbientTransaction)
        {
            _pending.Add(context, new OwnedTransaction(transactionOutcome));
        }
    }

    private async Task PushAsync(DbContext context, CancellationToken cancellationToken)
    {
        _pending.Remove(context);

        var values = Capture(context);
        if (values is null)
        {
            return;
        }

        var transactionOutcome = await HistoryWriterTransaction.BeginIfNeededAsync(context, HistoryWriter.Trigger, cancellationToken);

        try
        {
            var (connection, transaction) = Target(context);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            Bind(command, values);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            if (transactionOutcome.OwnedTransaction is { } transaction)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }

            await transactionOutcome.CloseAmbientConnectionAsync(context);
            throw;
        }

        if (transactionOutcome.OwnedTransaction is not null || transactionOutcome.OpenedConnectionForAmbientTransaction)
        {
            _pending.Add(context, new OwnedTransaction(transactionOutcome));
        }
    }

    // The five (key, value) pairs to push, or null when the change-context feature is not in use or
    // nothing temporal is being saved — in which case no transaction is forced and the provider is not
    // consulted. When it is in use, all five keys are always pushed (empty string for a null member,
    // which the trigger's nullif() maps back to NULL) so a later SaveChanges in the same caller
    // transaction fully overrides the previous one's context rather than inheriting stale values.
    private static List<KeyValuePair<string, string>>? Capture(DbContext context)
    {
        if (!HasTemporalChange(context))
        {
            return null;
        }

        var provider = ChangeContextProviderResolver.Resolve(context);
        var scopedReason = ChangeReasonScope.CurrentFor(context);
        if (provider is null && scopedReason is null)
        {
            return null;
        }

        var changeContext = provider?.GetChangeContext(context) ?? ChangeContext.Empty;
        if (scopedReason is not null)
        {
            changeContext = changeContext with { Reason = scopedReason };
        }

        return
        [
            new(HistoryTriggerSqlGenerator.ChangedByKey, changeContext.UserId ?? string.Empty),
            new(HistoryTriggerSqlGenerator.ChangedByNameKey, changeContext.UserName ?? string.Empty),
            new(HistoryTriggerSqlGenerator.CorrelationIdKey, changeContext.CorrelationId ?? string.Empty),
            new(HistoryTriggerSqlGenerator.ReasonKey, changeContext.Reason ?? string.Empty),
            new(HistoryTriggerSqlGenerator.ExtraKey, changeContext.Extra ?? string.Empty),
        ];
    }

    // HistorySnapshotGuardInterceptor.Guard runs first on SaveChanges (registered before this
    // interceptor in HindsightOptionsExtension) and already walked ChangeTracker.Entries() once. With
    // AutoDetectChangesEnabled on — the default — that call already ran the one DetectChanges() pass
    // this SaveChanges needs; nothing mutates a tracked entity's properties between the two calls, so
    // redoing it here would just re-scan the same graph for the same answer. A benchmark
    // (ChangeTrackerOverheadBenchmarks in benchmarks/Hindsight.Benchmarks) showed this second scan
    // scaling linearly with the number of tracked-but-unrelated entities in the context — real cost,
    // not noise. Suppressing detection here is safe either way: if the caller left
    // AutoDetectChangesEnabled on, Guard's call already did the work; if the caller turned it off
    // themselves, this is a no-op. Always restored in `finally`, never left disabled for the rest of
    // SaveChanges.
    private static bool HasTemporalChange(DbContext context)
    {
        var tracker = context.ChangeTracker;
        var autoDetectChangesEnabled = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in tracker.Entries())
            {
                if (entry.Metadata.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is true
                    && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    private static (DbConnection Connection, DbTransaction? Transaction) Target(DbContext context)
        => (context.Database.GetDbConnection(), context.Database.CurrentTransaction?.GetDbTransaction());

    // set_config keys are compile-time constants; only the values are parameterised.
    private static void Bind(DbCommand command, List<KeyValuePair<string, string>> values)
    {
        var calls = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var name = "v" + i.ToString(CultureInfo.InvariantCulture);
            calls[i] = $"set_config('{values[i].Key}', @{name}, true)";

            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = values[i].Value;
            command.Parameters.Add(parameter);
        }

        command.CommandText = "SELECT " + string.Join(", ", calls);
    }

    private sealed record OwnedTransaction(HistoryWriterTransaction.Outcome TransactionOutcome);
}
