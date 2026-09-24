using Hindsight;
using Microsoft.EntityFrameworkCore;
using ProductCatalogSample;

namespace HistoryViewerSample;

/// <summary>
/// Gives the history screen something to show: three products, several versions each, three users,
/// a reason on every change, a delete, and one bulk <c>ExecuteUpdate</c> with no change context.
/// </summary>
public static class Seed
{
    // HistoryWriter.Trigger stamps each change with PostgreSQL's now(), which the app can't set, so the
    // whole history is written during startup. A pause between steps keeps versions a second apart, so
    // the timeline reads as a sequence and AsOf has room to land between two versions.
    private static readonly TimeSpan _pause = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(CatalogDbContext db)
    {
        var widget = new Product { Sku = "WIDGET-1", Name = "Widget", Price = 19.99m, StockQuantity = 100 };
        var gadget = new Product { Sku = "GADGET-1", Name = "Gadget", Price = 9.00m, StockQuantity = 25 };
        var gizmo = new Product { Sku = "GIZMO-1", Name = "Gizmo", Price = 49.00m, StockQuantity = 10 };

        await StepAsync(db, "alice", "Alice", "Initial catalog import", () => db.Products.AddRange(widget, gadget));
        await StepAsync(db, "bob", "Bob", "Supplier cost increase", () => widget.Price = 24.99m);
        await StepAsync(db, "alice", "Alice", "Stock count", () => gadget.StockQuantity = 12);
        await StepAsync(db, "carol", "Carol", "Rebrand", () => widget.Name = "Widget Pro");
        await StepAsync(db, "bob", "Bob", "New supplier", () => db.Products.Add(gizmo));
        await StepAsync(db, "alice", "Alice", "Autumn sale", () =>
        {
            widget.Price = 19.99m;
            widget.StockQuantity = 60;
        });
        await StepAsync(db, "alice", "Alice", "Discontinued", () => db.Products.Remove(gadget));

        // A bulk write straight in SQL: no SaveChanges, so neither the change-context provider nor
        // WithReason runs. The trigger still records a version for every row it touches, with NULL in
        // every change-context column; the timeline shows it as a change nobody signed.
        await Task.Delay(_pause);
        var repriced = await db.Products.ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Price, p => Math.Round(p.Price * 1.10m, 2)));
        Console.WriteLine($"  (bulk) ExecuteUpdate: +10% on {repriced} products, no change context");
    }

    private static async Task StepAsync(CatalogDbContext db, string userId, string userName, string reason, Action change)
    {
        await Task.Delay(_pause);
        using (CurrentUser.Act(userId, userName))
        using (db.WithReason(reason))
        {
            change();
            await db.SaveChangesAsync();
        }

        Console.WriteLine($"  {userName,-6} {reason}");
    }
}
