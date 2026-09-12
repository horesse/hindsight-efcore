using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hindsight.Writers;

/// <summary>
/// The <see cref="HistoryWriter.Interceptor"/> implementation. On <c>SaveChanges</c> it snapshots the
/// tracked temporal entities; once the data change has hit the database — so store-generated keys are
/// known — it writes the history rows on the same connection and in the same transaction, opening one
/// if the caller did not (via <see cref="HistoryWriterTransaction"/>, which also rejects a configured
/// retrying execution strategy in that case — such a transaction would not survive a retry). One
/// timestamp per <c>SaveChanges</c>, taken from the registered <see cref="TimeProvider"/>. Intervals
/// are half-open <c>[valid_from, valid_to)</c> (DESIGN.md D3, D5).
/// </summary>
internal sealed class HistoryWriterInterceptor : SaveChangesInterceptor
{
    // State that must survive from SavingChanges to SavedChanges. Keyed by the context instance:
    // EF Core forbids concurrent operations on one context, so there is at most one live entry per
    // context, removed in SavedChanges / SaveChangesFailed. Deliberately not a plain field — the
    // interceptor is shared between contexts and a field would race (CLAUDE.md rule 5).
    private readonly ConditionalWeakTable<DbContext, SaveState> _pending = new();

    // Parameterless-constructor factories for change context providers that are not registered in the
    // application service provider. Compiled once per type, not per SaveChanges (library-code rule:
    // no reflection on the hot path).
    private static readonly ConcurrentDictionary<Type, Func<object>> _providerActivators = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && IsEnabled(context))
        {
            Prepare(context);
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
            await PrepareAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var state))
        {
            _pending.Remove(context);
            Complete(context, state);
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var state))
        {
            _pending.Remove(context);
            await CompleteAsync(context, state, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var state))
        {
            _pending.Remove(context);
            RollbackAndDispose(context, state.TransactionOutcome);
        }

        base.SaveChangesFailed(eventData);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var state))
        {
            _pending.Remove(context);
            await RollbackAndDisposeAsync(context, state.TransactionOutcome, cancellationToken);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static bool IsEnabled(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<HindsightOptionsExtension>()?.HistoryWriter
            == HistoryWriter.Interceptor;

    private static TimeProvider ResolveTimeProvider(DbContext context)
    {
        var applicationServiceProvider = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()
            ?.ApplicationServiceProvider;

        return applicationServiceProvider?.GetService(typeof(TimeProvider)) as TimeProvider
            ?? TimeProvider.System;
    }

    private void Prepare(DbContext context)
    {
        var rows = Snapshot(context, out var timestamp, out var changeContext);
        if (rows is null)
        {
            return;
        }

        var transactionOutcome = HistoryWriterTransaction.BeginIfNeeded(context, HistoryWriter.Interceptor);

        try
        {
            // Must run after the transaction above is open (it needs to lock rows that the DELETE
            // about to execute in the same SaveChanges will also touch) and before this method returns
            // control to EF Core, which sends that DELETE next.
            DeletedRowSnapshotReader.Read(context, rows);
        }
        catch
        {
            RollbackAndDispose(context, transactionOutcome);
            throw;
        }

        _pending.AddOrUpdate(context, new SaveState(timestamp, changeContext, rows, transactionOutcome));
    }

    private async Task PrepareAsync(DbContext context, CancellationToken cancellationToken)
    {
        var rows = Snapshot(context, out var timestamp, out var changeContext);
        if (rows is null)
        {
            return;
        }

        var transactionOutcome = await HistoryWriterTransaction.BeginIfNeededAsync(
            context, HistoryWriter.Interceptor, cancellationToken);

        try
        {
            await DeletedRowSnapshotReader.ReadAsync(context, rows, cancellationToken);
        }
        catch
        {
            await RollbackAndDisposeAsync(context, transactionOutcome, cancellationToken);
            throw;
        }

        _pending.AddOrUpdate(context, new SaveState(timestamp, changeContext, rows, transactionOutcome));
    }

    private static void RollbackAndDispose(DbContext context, HistoryWriterTransaction.Outcome transactionOutcome)
    {
        if (transactionOutcome.OwnedTransaction is { } transaction)
        {
            transaction.Rollback();
            transaction.Dispose();
        }

        transactionOutcome.CloseAmbientConnection(context);
    }

    private static async Task RollbackAndDisposeAsync(
        DbContext context, HistoryWriterTransaction.Outcome transactionOutcome, CancellationToken cancellationToken)
    {
        if (transactionOutcome.OwnedTransaction is { } transaction)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
        }

        await transactionOutcome.CloseAmbientConnectionAsync(context);
    }

    private IReadOnlyList<PendingHistoryRow>? Snapshot(
        DbContext context, out DateTimeOffset timestamp, out ChangeContext changeContext)
    {
        timestamp = default;
        changeContext = ChangeContext.Empty;

        // A stale entry means a previous SaveChanges threw between SavingChanges and its terminal
        // hook, or a nested save is running. Drop it and start fresh.
        _pending.Remove(context);

        var rows = HistoryRowPlan.BuildPending(context);
        if (rows.Count == 0)
        {
            return null;
        }

        timestamp = ResolveTimeProvider(context).GetUtcNow();
        changeContext = CaptureChangeContext(context);
        return rows;
    }

    // One provider call per SaveChanges (never per row), taken here alongside the timestamp. A reason
    // set with DbContext.WithReason(...) overrides whatever the provider put in ChangeContext.Reason.
    private static ChangeContext CaptureChangeContext(DbContext context)
    {
        var changeContext = ResolveChangeContextProvider(context)?.GetChangeContext(context)
            ?? ChangeContext.Empty;

        if (ChangeReasonScope.CurrentFor(context) is { } scopedReason)
        {
            changeContext = changeContext with { Reason = scopedReason };
        }

        return changeContext;
    }

    private static IChangeContextProvider? ResolveChangeContextProvider(DbContext context)
    {
        var providerType = context.GetService<IDbContextOptions>()
            .FindExtension<HindsightOptionsExtension>()
            ?.ChangeContextProviderType;
        if (providerType is null)
        {
            return null;
        }

        var applicationServiceProvider = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()
            ?.ApplicationServiceProvider;

        if (applicationServiceProvider?.GetService(providerType) is IChangeContextProvider fromServices)
        {
            return fromServices;
        }

        var activator = _providerActivators.GetOrAdd(providerType, CreateActivator);
        return (IChangeContextProvider)activator();
    }

    private static Func<object> CreateActivator(Type providerType)
    {
        if (providerType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"Change context provider '{providerType.FullName}' is not registered on the "
                + "application service provider and has no parameterless constructor. Register it with "
                + "the DbContext's application service provider, or give it a parameterless constructor.");
        }

        return () => Activator.CreateInstance(providerType)!;
    }

    private static void Complete(DbContext context, SaveState state)
    {
        try
        {
            HistoryRowPlan.FillGeneratedValues(state.Rows);
            HistoryRowWriter.Write(context, state.Timestamp, state.ChangeContext, state.Rows);
            state.TransactionOutcome.OwnedTransaction?.Commit();
        }
        catch
        {
            state.TransactionOutcome.OwnedTransaction?.Rollback();
            RestoreEntityStates(state.Rows);
            throw;
        }
        finally
        {
            state.TransactionOutcome.OwnedTransaction?.Dispose();
            state.TransactionOutcome.CloseAmbientConnection(context);
        }
    }

    private static async Task CompleteAsync(DbContext context, SaveState state, CancellationToken cancellationToken)
    {
        try
        {
            HistoryRowPlan.FillGeneratedValues(state.Rows);
            await HistoryRowWriter.WriteAsync(
                context, state.Timestamp, state.ChangeContext, state.Rows, cancellationToken);

            if (state.TransactionOutcome.OwnedTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (state.TransactionOutcome.OwnedTransaction is { } transaction)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            RestoreEntityStates(state.Rows);
            throw;
        }
        finally
        {
            if (state.TransactionOutcome.OwnedTransaction is { } transaction)
            {
                await transaction.DisposeAsync();
            }

            await state.TransactionOutcome.CloseAmbientConnectionAsync(context);
        }
    }

    // By the time this runs (SavedChanges/SavedChangesAsync), EF Core's own SaveChanges pipeline has
    // already called ChangeTracker.AcceptAllChanges() — Added/Modified entries are now Unchanged, and
    // Deleted entries are detached — strictly before this interceptor gets a chance to fail. If the
    // history write (or the commit) then fails, both the data change and the history rows roll back
    // together (the interceptor owns the transaction), but without this, the caller's tracked entities
    // would silently read as saved: a caught exception followed by a plain retry would find nothing
    // Added/Modified/Deleted left to save, and do nothing. Restoring the pre-accept EntityState here
    // makes a retry actually retry. A Deleted row's Entry is deliberately null (PendingHistoryRow) —
    // its entry was already detached by AcceptAllChanges and there is nothing left in the tracker to
    // re-mark; that case is a documented caveat (docs/articles/limitations.md), not fixed here.
    private static void RestoreEntityStates(IReadOnlyList<PendingHistoryRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Entry is { } entry)
            {
                entry.State = row.State;
            }
        }
    }

    private sealed record SaveState(
        DateTimeOffset Timestamp,
        ChangeContext ChangeContext,
        IReadOnlyList<PendingHistoryRow> Rows,
        HistoryWriterTransaction.Outcome TransactionOutcome);
}
