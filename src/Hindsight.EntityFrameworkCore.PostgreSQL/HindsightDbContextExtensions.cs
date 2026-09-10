using Hindsight.Writers;
using Microsoft.EntityFrameworkCore;

namespace Hindsight;

/// <summary>
/// <see cref="DbContext"/> extensions for supplying change context at call sites.
/// </summary>
public static class HindsightDbContextExtensions
{
    /// <summary>
    /// Opens a scope that sets the <c>reason</c> on every history row written by <c>SaveChanges</c>
    /// calls made inside it, overriding <see cref="ChangeContext.Reason"/> from the registered
    /// <see cref="IChangeContextProvider"/> (if any). Dispose the returned handle — normally with
    /// <c>using</c> — to end the scope; scopes nest and the innermost one wins.
    /// </summary>
    /// <param name="context">The context whose subsequent <c>SaveChanges</c> calls get the reason.</param>
    /// <param name="reason">The reason to record. Must not be null or whitespace.</param>
    /// <returns>A handle that ends the scope when disposed.</returns>
    /// <remarks>
    /// The scope flows with the async context, not with <paramref name="context"/>: it affects any
    /// <c>SaveChanges</c> on the same asynchronous control flow. This works without an
    /// <see cref="IChangeContextProvider"/> registered — the other context columns stay
    /// <see langword="null"/> and only <c>reason</c> is filled.
    /// </remarks>
    public static IDisposable WithReason(this DbContext context, string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return ChangeReasonScope.Push(reason);
    }
}
