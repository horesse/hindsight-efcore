namespace Hindsight.Writers;

/// <summary>
/// Ambient <c>reason</c> override for the change context, set by
/// <see cref="HindsightDbContextExtensions.WithReason(Microsoft.EntityFrameworkCore.DbContext, string)"/>.
/// The value flows with the async context, so a <c>using</c> block around <c>SaveChangesAsync</c>
/// works. Scopes nest: <see cref="Push"/> stashes the current value and <see cref="IDisposable.Dispose"/>
/// restores it, so the innermost open scope wins.
/// </summary>
internal static class ChangeReasonScope
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The reason set by the nearest enclosing <see cref="Push"/> scope, or <see langword="null"/>.</summary>
    public static string? Current => _current.Value;

    /// <summary>
    /// Sets <see cref="Current"/> to <paramref name="reason"/> until the returned handle is disposed,
    /// at which point the previous value is restored.
    /// </summary>
    public static IDisposable Push(string reason)
    {
        var previous = _current.Value;
        _current.Value = reason;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current.Value = previous;
        }
    }
}
