using Hindsight;
using Microsoft.EntityFrameworkCore;

namespace HistoryViewerSample;

/// <summary>
/// Stands in for "who's making this change" while the seed runs at startup, outside any HTTP request.
/// A real app reads the user from <c>IHttpContextAccessor</c> instead - see docs/writing/change-context.md.
/// </summary>
public static class CurrentUser
{
    private static readonly AsyncLocal<(string Id, string Name)?> _user = new();

    public static IDisposable Act(string id, string name)
    {
        var previous = _user.Value;
        _user.Value = (id, name);
        return new Restore(() => _user.Value = previous);
    }

    public static string? Id => _user.Value?.Id;

    public static string? Name => _user.Value?.Name;

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

public sealed class DemoChangeContextProvider : IChangeContextProvider
{
    // Registered as a singleton and called once per SaveChanges that writes a temporal entity -
    // read ambient, per-operation state here, never cache it in a field.
    public ChangeContext GetChangeContext(DbContext context) =>
        new() { ChangedBy = CurrentUser.Id, ChangedByName = CurrentUser.Name };
}
