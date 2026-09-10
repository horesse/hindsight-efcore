using Microsoft.EntityFrameworkCore;

namespace Hindsight.Benchmarks;

/// <summary>
/// Returns a constant <see cref="ChangeContext"/> with no I/O, so the change-context benchmarks
/// measure the cost of the plumbing — the extra provider call and wider history <c>INSERT</c> for
/// the interceptor, the <c>set_config</c> round-trip for the trigger — and not the cost of building
/// a context.
/// </summary>
public sealed class FixedChangeContextProvider : IChangeContextProvider
{
    private static readonly ChangeContext _context = new()
    {
        UserId = "bench-user",
        UserName = "Bench User",
        CorrelationId = "11111111-1111-1111-1111-111111111111",
        Reason = "benchmark",
    };

    /// <inheritdoc />
    public ChangeContext GetChangeContext(DbContext context) => _context;
}
