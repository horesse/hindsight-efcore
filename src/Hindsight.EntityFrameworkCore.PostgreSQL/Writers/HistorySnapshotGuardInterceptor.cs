using Hindsight.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.Writers;

/// <summary>
/// Enforces the second half of DESIGN.md D7: a historical query result is read-only. An entity
/// instance handed out by <c>AsOf()</c> / <c>AllVersions()</c> / <c>History&lt;T&gt;()</c> (marked via
/// <see cref="HistoryOrigin"/>) that is then re-attached — <c>Update</c>, <c>Attach</c>, <c>Add</c>,
/// <c>Remove</c>, or a manual state change — and saved would silently write a stale snapshot back as
/// the current version and generate spurious history (CLAUDE.md rule 2). This interceptor runs first
/// on <c>SaveChanges</c>, before the history writer, and throws
/// <see cref="InvalidOperationException"/> if it finds such an instance being persisted.
/// </summary>
/// <remarks>
/// Registered in both writer modes (there is no history writer running in <c>Trigger</c> mode to
/// catch this otherwise). Legitimate saves are untouched: a hand-built instance, one loaded with
/// <c>Find</c> / a normal query, or values copied off a snapshot onto a fresh instance are never
/// marked.
/// </remarks>
internal sealed class HistorySnapshotGuardInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (HistoryOrigin.IsFromHistory(entry.Entity))
            {
                throw new InvalidOperationException(
                    $"The '{entry.Metadata.DisplayName()}' instance being saved was read from a Hindsight "
                    + "historical query (AsOf(), AllVersions() or History<T>()). Historical results are "
                    + "read-only (DESIGN.md D7): re-attaching one and calling SaveChanges would write a "
                    + "past snapshot back as the current version. Copy the values you need onto a new "
                    + "instance, or onto one loaded with a normal query / DbSet.Find(), and save that.");
            }
        }
    }
}
