using Hindsight;
using Microsoft.EntityFrameworkCore;
using ProductCatalogSample;

namespace HistoryViewerSample;

/// <summary>A product as the list page shows it: its newest history row and how many versions it has.</summary>
public sealed record ProductSummary(Version<Product> Latest, int VersionCount)
{
    public bool IsDeleted => Latest.Operation == VersionOperation.Delete;
}

/// <summary>One row of the timeline: a history row and what it changed against the version before it.</summary>
public sealed record TimelineEntry(Version<Product> Version, IReadOnlyList<PropertyChange> Changes);

/// <summary>
/// Every query the history screen runs. The pages only render what these return; the Hindsight calls
/// all live here, so the docs can import them.
/// </summary>
public sealed class HistoryQueries(CatalogDbContext db)
{
    #region product-list
    // Every product that ever existed, deleted ones included: a history screen that hides deleted rows
    // can't answer "what happened to GADGET-1?". History<T>() returns newest first, so the first row
    // per product is its latest state, or its delete tombstone.
    // Grouped in memory: fine for a sample; a real screen would page this.
    public async Task<IReadOnlyList<ProductSummary>> ListProductsAsync(CancellationToken ct = default)
    {
        var history = await db.History<Product>().ToListAsync(ct);
        return history
            .GroupBy(v => v.Entity.Id)
            .Select(g => new ProductSummary(g.First(), g.Count(v => v.Operation != VersionOperation.Delete)))
            .OrderBy(s => s.Latest.Entity.Sku, StringComparer.Ordinal)
            .ToList();
    }
    #endregion product-list

    #region timeline
    // The audit view: every history row of one product, newest first, delete tombstone included, with
    // who, when and why. Each row is diffed against the state version before it.
    public async Task<IReadOnlyList<TimelineEntry>> TimelineAsync(int productId, CancellationToken ct = default)
    {
        var versions = await db.History<Product>()
            .Where(v => v.Entity.Id == productId)
            .ToListAsync(ct);

        var timeline = new List<TimelineEntry>(versions.Count);
        Version<Product>? older = null;
        foreach (var version in Enumerable.Reverse(versions)) // oldest first, to diff forwards
        {
            // A delete tombstone repeats the last state's values, so Diff rejects it: the delete is read
            // from Operation. `older == null` diffs the insert against nothing.
            var changes = version.Operation == VersionOperation.Delete ? [] : db.Diff(older, version);
            timeline.Add(new TimelineEntry(version, changes));
            if (version.Operation != VersionOperation.Delete)
            {
                older = version;
            }
        }

        timeline.Reverse(); // newest first again, for display
        return timeline;
    }
    #endregion timeline

    #region as-of
    // The product exactly as it was at `at`, or null if it didn't exist then (not created yet, or
    // already deleted).
    public Task<Product?> AsOfAsync(int productId, DateTimeOffset at, CancellationToken ct = default) =>
        db.Products.AsOf(at).SingleOrDefaultAsync(p => p.Id == productId, ct);
    #endregion as-of
}
