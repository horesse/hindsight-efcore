using Hindsight;
using Microsoft.EntityFrameworkCore;

namespace TaskTrackerSample;

/// <summary>
/// Stands in for "who's making this change" in a console app. A real app reads this from
/// <c>IHttpContextAccessor</c> or similar - see docs/writing/change-context.md.
/// </summary>
public static class CurrentUser
{
    private static readonly AsyncLocal<string?> _name = new();

    public static IDisposable Act(string name)
    {
        var previous = _name.Value;
        _name.Value = name;
        return new Restore(() => _name.Value = previous);
    }

    public static string? Name => _name.Value;

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

#region provider
public sealed class DemoChangeContextProvider : IChangeContextProvider
{
    // Registered as a singleton and called once per SaveChanges that writes a temporal entity -
    // read ambient, per-operation state here, never cache it in a field.
    public ChangeContext GetChangeContext(DbContext context) =>
        new() { UserName = CurrentUser.Name };
}
#endregion provider
