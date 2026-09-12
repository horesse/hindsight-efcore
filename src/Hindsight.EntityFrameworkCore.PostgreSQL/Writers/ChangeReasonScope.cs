using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Writers;

/// <summary>
/// Per-<see cref="DbContext"/> <c>reason</c> override for the change context, set by
/// <see cref="HindsightDbContextExtensions.WithReason(Microsoft.EntityFrameworkCore.DbContext, string)"/>.
/// Keyed by the context instance — not by the async control flow — so a scope opened on one
/// <see cref="DbContext"/> cannot affect a <c>SaveChanges</c> on a different instance, even when both
/// run on the same logical async flow. Scopes nest: <see cref="Push"/> stashes the value previously
/// held for that instance and <see cref="IDisposable.Dispose"/> restores it, so the innermost open
/// scope for a given context wins, regardless of the order in which sibling scopes on other contexts
/// are disposed.
/// </summary>
internal static class ChangeReasonScope
{
    // ConditionalWeakTable, not a Dictionary: this must not keep a DbContext alive after its own
    // lifetime ends, and must not leak an entry for a context that is later garbage collected. Same
    // rationale as HistoryWriterInterceptor's own per-context state (_pending).
    private static readonly ConditionalWeakTable<DbContext, Cell> _cells = new();

    /// <summary>
    /// The reason set by the nearest enclosing <see cref="Push"/> scope for <paramref name="context"/>,
    /// or <see langword="null"/> if none is open.
    /// </summary>
    public static string? CurrentFor(DbContext context)
        => _cells.TryGetValue(context, out var cell) ? cell.Value : null;

    /// <summary>
    /// Sets <see cref="CurrentFor"/> for <paramref name="context"/> to <paramref name="reason"/> until
    /// the returned handle is disposed, at which point the value previously held for that context is
    /// restored.
    /// </summary>
    public static IDisposable Push(DbContext context, string reason)
    {
        var cell = _cells.GetValue(context, static _ => new Cell());
        var previous = cell.Value;
        cell.Value = reason;
        return new Restore(cell, previous);
    }

    private sealed class Cell
    {
        public string? Value;
    }

    private sealed class Restore(Cell cell, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cell.Value = previous;
        }
    }
}
