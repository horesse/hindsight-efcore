using Hindsight;
using Microsoft.EntityFrameworkCore;
using ProductCatalogSample;
using Testcontainers.PostgreSql;

// A self-contained tour of Hindsight's default writer (`HistoryWriter.Interceptor`) and its query API:
// AsOf, AllVersions and History<T>. No setup required - this starts and tears down its own disposable
// PostgreSQL container, so `dotnet run` just works.

Console.WriteLine("Starting a disposable PostgreSQL 17 container (needs Docker)...");
await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
await container.StartAsync();

var options = new DbContextOptionsBuilder<CatalogDbContext>()
    .UseNpgsql(container.GetConnectionString())
    .UseHindsight() // HistoryWriter.Interceptor is the default.
    .Options;

await using var db = new CatalogDbContext(options);
// EnsureCreatedAsync, not MigrateAsync: this disposable container never needs `dotnet ef database
// update` against it, and it's the same schema-creation path the integration test suite uses. The
// checked-in Migrations/ folder is here so `dotnet ef migrations add`/`script` works against this
// model, same as any real project's.
await db.Database.EnsureCreatedAsync();
Console.WriteLine("Schema created. `products` and `products_history` now exist side by side.");
Console.WriteLine();

#region create
var widget = new Product
{
    Sku = "WIDGET-1",
    Name = "Widget",
    Price = 19.99m,
    StockQuantity = 100,
    UpdatedAt = DateTimeOffset.UtcNow,
};
db.Products.Add(widget);
await db.SaveChangesAsync();
var createdAt = DateTimeOffset.UtcNow;
Console.WriteLine($"Created '{widget.Name}' at {Money(widget.Price)} — that's version 1.");
#endregion create

await Task.Delay(50); // keeps the timestamps below comfortably distinct for the console narration.

#region price-change
widget.Price = 24.99m;
await db.SaveChangesAsync();
var priceChangedAt = DateTimeOffset.UtcNow;
Console.WriteLine($"Raised the price to {Money(widget.Price)} — version 2, no code change to the query below.");
#endregion price-change

await Task.Delay(50);

#region sale
widget.Price = 14.99m;
widget.StockQuantity = 40;
await db.SaveChangesAsync();
Console.WriteLine($"Put it on sale at {Money(widget.Price)}, stock now {widget.StockQuantity} — version 3.");
Console.WriteLine();
#endregion sale

#region as-of
// What did this product look like right after launch, before the sale?
var atLaunch = await db.Products.AsOf(priceChangedAt).SingleAsync(p => p.Id == widget.Id);
Console.WriteLine($"AsOf(right after launch): {atLaunch.Name} was {Money(atLaunch.Price)}, stock {atLaunch.StockQuantity}.");
#endregion as-of

#region all-versions
Console.WriteLine();
Console.WriteLine("AllVersions(), newest first:");
await foreach (var version in db.Products.AllVersions().Where(p => p.Id == widget.Id).AsAsyncEnumerable())
{
    Console.WriteLine($"  {Money(version.Price),8}  stock {version.StockQuantity,4}");
}
#endregion all-versions

#region history
Console.WriteLine();
Console.WriteLine("History<Product>() — every version with its validity window:");
await foreach (var v in db.History<Product>().Where(v => v.Entity.Id == widget.Id).AsAsyncEnumerable())
{
    var to = v.IsCurrent ? "now" : v.ValidTo.ToString("O");
    Console.WriteLine($"  [{v.ValidFrom:O} .. {to}]  {v.Operation,-6}  {Money(v.Entity.Price),8}");
}
#endregion history

#region delete
db.Products.Remove(widget);
await db.SaveChangesAsync();
Console.WriteLine();
Console.WriteLine("Deleted the product. It's gone from `products`, but its history isn't:");
var tombstone = await db.History<Product>()
    .Where(v => v.Entity.Id == widget.Id && v.Operation == VersionOperation.Delete)
    .SingleAsync();
Console.WriteLine($"  tombstone at {tombstone.ValidFrom:O}, last price on record: {Money(tombstone.Entity.Price)}");
#endregion delete

Console.WriteLine();
Console.WriteLine($"Created at {createdAt:O}. `AsOf(before that)` would return no rows — try it.");

// `:C` needs a culture with a real currency symbol; InvariantGlobalization (Directory.Build.props) has
// none, so it prints '¤' instead of '$'. Formatting the number ourselves sidesteps that everywhere.
static string Money(decimal amount) => $"${amount:F2}";
