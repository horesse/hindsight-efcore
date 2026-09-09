using System.Runtime.CompilerServices;
using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// The <see cref="HistoryWriter.Interceptor"/> implementation. On <c>SaveChanges</c> it snapshots the
/// tracked temporal entities; once the data change has hit the database — so store-generated keys are
/// known — it writes the history rows on the same connection and in the same transaction, opening one
/// if the caller did not. One timestamp per <c>SaveChanges</c>, taken from the registered
/// <see cref="TimeProvider"/>. Intervals are half-open <c>[valid_from, valid_to)</c> (DESIGN.md D3, D5).
/// </summary>
internal sealed class HistoryWriterInterceptor : SaveChangesInterceptor
{
    // State that must survive from SavingChanges to SavedChanges. Keyed by the context instance:
    // EF Core forbids concurrent operations on one context, so there is at most one live entry per
    // context, removed in SavedChanges / SaveChangesFailed. Deliberately not a plain field — the
    // interceptor is shared between contexts and a field would race (CLAUDE.md rule 5).
    private readonly ConditionalWeakTable<DbContext, SaveState> _pending = new();

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
            if (state.OwnedTransaction is { } transaction)
            {
                transaction.Rollback();
                transaction.Dispose();
            }
        }

        base.SaveChangesFailed(eventData);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var state))
        {
            _pending.Remove(context);
            if (state.OwnedTransaction is { } transaction)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
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
        var rows = Snapshot(context, out var timestamp);
        if (rows is null)
        {
            return;
        }

        IDbContextTransaction? ownedTransaction = context.Database.CurrentTransaction is null
            ? context.Database.BeginTransaction()
            : null;

        _pending.AddOrUpdate(context, new SaveState(timestamp, rows, ownedTransaction));
    }

    private async Task PrepareAsync(DbContext context, CancellationToken cancellationToken)
    {
        var rows = Snapshot(context, out var timestamp);
        if (rows is null)
        {
            return;
        }

        IDbContextTransaction? ownedTransaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        _pending.AddOrUpdate(context, new SaveState(timestamp, rows, ownedTransaction));
    }

    private IReadOnlyList<PendingHistoryRow>? Snapshot(DbContext context, out DateTimeOffset timestamp)
    {
        timestamp = default;

        // A stale entry means a previous SaveChanges threw between SavingChanges and its terminal
        // hook, or a nested save is running. Drop it and start fresh.
        _pending.Remove(context);

        var rows = HistoryRowPlan.BuildPending(context);
        if (rows.Count == 0)
        {
            return null;
        }

        timestamp = ResolveTimeProvider(context).GetUtcNow();
        return rows;
    }

    private static void Complete(DbContext context, SaveState state)
    {
        try
        {
            HistoryRowPlan.FillGeneratedValues(state.Rows);
            HistoryRowWriter.Write(context, state.Timestamp, state.Rows);
            state.OwnedTransaction?.Commit();
        }
        catch
        {
            state.OwnedTransaction?.Rollback();
            throw;
        }
        finally
        {
            state.OwnedTransaction?.Dispose();
        }
    }

    private static async Task CompleteAsync(DbContext context, SaveState state, CancellationToken cancellationToken)
    {
        try
        {
            HistoryRowPlan.FillGeneratedValues(state.Rows);
            await HistoryRowWriter.WriteAsync(context, state.Timestamp, state.Rows, cancellationToken);

            if (state.OwnedTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (state.OwnedTransaction is { } transaction)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (state.OwnedTransaction is { } transaction)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private sealed record SaveState(
        DateTimeOffset Timestamp,
        IReadOnlyList<PendingHistoryRow> Rows,
        IDbContextTransaction? OwnedTransaction);
}
