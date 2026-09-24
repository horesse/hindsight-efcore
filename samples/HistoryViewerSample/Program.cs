using System.ComponentModel;
using System.Diagnostics;
using Hindsight;
using HistoryViewerSample;
using HistoryViewerSample.Components;
using Microsoft.EntityFrameworkCore;
using ProductCatalogSample;
using Testcontainers.PostgreSql;

// A read-only "history screen" over the ProductCatalogSample domain: the product list, one product's
// timeline (who, when, what and why), its state at any instant, and what each version changed.
// No setup required - this starts and tears down its own disposable PostgreSQL container, seeds it,
// and opens the page. Pass --no-browser to skip opening it.

const string Url = "http://localhost:5080";
var openBrowser = !args.Contains("--no-browser");

Console.WriteLine("Starting a disposable PostgreSQL 17 container (needs Docker)...");
await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
await container.StartAsync();

var builder = WebApplication.CreateBuilder(args.Where(a => a != "--no-browser").ToArray());
builder.WebHost.UseUrls(Url);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

builder.Services.AddDbContext<CatalogDbContext>(options => options
    .UseNpgsql(container.GetConnectionString())
    // Trigger, not the default Interceptor: a history screen has to show every change to a row, and
    // only the trigger sees writes that bypass SaveChanges (ExecuteUpdate, ExecuteDelete, raw SQL).
    // The seed ends with an ExecuteUpdate to put one on the timeline.
    .UseHindsight(h => h
        .UseHistoryWriter(HistoryWriter.Trigger)
        .WithChangeContext<DemoChangeContextProvider>()));
builder.Services.AddScoped<HistoryQueries>();
builder.Services.AddRazorComponents();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    // EnsureCreatedAsync, not MigrateAsync: the container is disposable, and EnsureCreated creates the
    // history triggers too. This sample has no Migrations/ folder of its own.
    await db.Database.EnsureCreatedAsync();
    Console.WriteLine("Seeding the catalog, one change a second...");
    await Seed.RunAsync(db);
}

app.UseAntiforgery();
app.MapRazorComponents<App>();

if (openBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No desktop browser to open (a container, an SSH session): the URL is printed below.
        }
    });
}

Console.WriteLine($"History viewer: {Url}  (Ctrl+C stops it and removes the container)");
await app.RunAsync();
