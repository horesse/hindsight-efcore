using Microsoft.EntityFrameworkCore;

namespace Hindsight;

/// <summary>
/// Supplies the <see cref="ChangeContext"/> (user, correlation id, reason, extra) that the
/// <see cref="HistoryWriter.Interceptor"/> writer stamps onto the history rows of a <c>SaveChanges</c>.
/// Register an implementation with
/// <see cref="HindsightOptionsBuilder.WithChangeContext{TProvider}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GetChangeContext"/> is called <b>once per <c>SaveChanges</c></b>, on the thread that
/// called it, after the tracked temporal changes have been snapshotted and before any history row is
/// written — never once per row. An implementation that has nothing to record returns
/// <see cref="ChangeContext.Empty"/>; an exception thrown from it propagates out of <c>SaveChanges</c>
/// and the whole transaction (data change included) rolls back.
/// </para>
/// <para>
/// The method is synchronous by design: it runs inside both <c>SaveChanges</c> and
/// <c>SaveChangesAsync</c>, and blocking on an async source there would be sync-over-async. Resolve
/// any async state before <c>SaveChanges</c> and hand it to the provider, or cache it.
/// </para>
/// </remarks>
public interface IChangeContextProvider
{
    /// <summary>
    /// Returns the change context for the <c>SaveChanges</c> currently running on
    /// <paramref name="context"/>. Return <see cref="ChangeContext.Empty"/> (or a
    /// <see cref="ChangeContext"/> with only some members set) when there is nothing, or nothing
    /// more, to record.
    /// </summary>
    /// <param name="context">The <see cref="DbContext"/> whose <c>SaveChanges</c> is in progress.</param>
    /// <returns>The context to stamp onto this call's history rows; never <see langword="null"/>.</returns>
    ChangeContext GetChangeContext(DbContext context);
}
