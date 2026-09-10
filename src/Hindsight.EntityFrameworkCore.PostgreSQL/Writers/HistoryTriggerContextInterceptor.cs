using System.Collections.Concurrent;
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
/// It also opens a transaction when the caller has none, exactly as the Interceptor writer does: EF
/// Core's default <c>AutoTransactionBehavior.WhenNeeded</c> runs a single-statement <c>SaveChanges</c>
/// without a transaction, and <c>set_config(..., true)</c> outside a transaction has no effect. The
/// transaction it opens is committed in <c>SavedChanges</c> and rolled back in
/// <c>SaveChangesFailed</c>, so the data change, the trigger's history rows and the context all commit
/// together. Bulk operations (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>) do not pass through here: the
/// trigger still records their history, with <see langword="null"/> context columns (DESIGN.md D4).
/// </remarks>
internal sealed class HistoryTriggerContextInterceptor : SaveChangesInterceptor
{
    // Keyed by the context instance, like HistoryWriterInterceptor: at most one live entry per context,
    // removed in the terminal hook. Never a field — the interceptor is shared between contexts.
    private readonly ConditionalWeakTable<DbContext, OwnedTransaction> _pending = new();

    private static readonly ConcurrentDictionary<Type, Func<object>> _providerActivators = new();

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
            owned.Transaction.Commit();
            owned.Transaction.Dispose();
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            await owned.Transaction.CommitAsync(cancellationToken);
            await owned.Transaction.DisposeAsync();
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            owned.Transaction.Rollback();
            owned.Transaction.Dispose();
        }

        base.SaveChangesFailed(eventData);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var owned))
        {
            _pending.Remove(context);
            await owned.Transaction.RollbackAsync(cancellationToken);
            await owned.Transaction.DisposeAsync();
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

        var owned = context.Database.CurrentTransaction is null
            ? context.Database.BeginTransaction()
            : null;

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
            owned?.Rollback();
            owned?.Dispose();
            throw;
        }

        if (owned is not null)
        {
            _pending.Add(context, new OwnedTransaction(owned));
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

        var owned = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

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
            if (owned is not null)
            {
                await owned.RollbackAsync(cancellationToken);
                await owned.DisposeAsync();
            }

            throw;
        }

        if (owned is not null)
        {
            _pending.Add(context, new OwnedTransaction(owned));
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

        var provider = ResolveProvider(context);
        var scopedReason = ChangeReasonScope.Current;
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

    private static bool HasTemporalChange(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Metadata.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is true
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                return true;
            }
        }

        return false;
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

    private static IChangeContextProvider? ResolveProvider(DbContext context)
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

    private sealed record OwnedTransaction(IDbContextTransaction Transaction);
}
