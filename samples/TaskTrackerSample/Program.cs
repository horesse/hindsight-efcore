using Hindsight;
using Microsoft.EntityFrameworkCore;
using TaskTrackerSample;
using Testcontainers.PostgreSql;

// A self-contained tour of `HistoryWriter.Trigger` and change context: who changed a row, and why -
// plus what happens to a raw SQL / ExecuteUpdate write that never goes through SaveChanges at all.
// No setup required - this starts and tears down its own disposable PostgreSQL container.

Console.WriteLine("Starting a disposable PostgreSQL 17 container (needs Docker)...");
await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
await container.StartAsync();

var options = new DbContextOptionsBuilder<TrackerDbContext>()
    .UseNpgsql(container.GetConnectionString())
    .UseHindsight(h => h
        .UseHistoryWriter(HistoryWriter.Trigger)
        .WithChangeContext<DemoChangeContextProvider>())
    .Options;

await using var db = new TrackerDbContext(options);
// EnsureCreatedAsync, not MigrateAsync: this disposable container never needs `dotnet ef database
// update` against it, and it's the same schema-creation path the integration test suite uses. The
// checked-in Migrations/ folder is here so `dotnet ef migrations add`/`script` works against this
// model, same as any real project's.
await db.Database.EnsureCreatedAsync();
Console.WriteLine("Schema created. The trigger on `work_items` writes `work_items_history` for every change.");
Console.WriteLine();

#region create-as-alice
var item = new WorkItem { Title = "Write the Hindsight blog post", Status = WorkItemStatus.Todo, UpdatedAt = DateTimeOffset.UtcNow };
using (CurrentUser.Act("alice"))
using (db.WithReason("Sprint planning"))
{
    db.WorkItems.Add(item);
    await db.SaveChangesAsync();
}
Console.WriteLine($"alice created '{item.Title}'.");
#endregion create-as-alice

#region status-changes
using (CurrentUser.Act("bob"))
using (db.WithReason("Picked it up"))
{
    item.Status = WorkItemStatus.InProgress;
    await db.SaveChangesAsync();
}
Console.WriteLine("bob moved it to InProgress.");

using (CurrentUser.Act("alice"))
using (db.WithReason("Reviewed and merged"))
{
    item.Status = WorkItemStatus.Done;
    await db.SaveChangesAsync();
}
Console.WriteLine("alice marked it Done.");
#endregion status-changes

#region audit-trail
Console.WriteLine();
Console.WriteLine("History<WorkItem>() — who changed it, and why:");
await foreach (var v in db.History<WorkItem>().Where(v => v.Entity.Id == item.Id).AsAsyncEnumerable())
{
    Console.WriteLine($"  {v.Entity.Status,-11} by {v.ChangedByName ?? "(unknown)",-6} — {v.Reason}");
}
#endregion audit-trail

#region bulk-write
// A couple more items, created without going through the change-context machinery above.
db.WorkItems.AddRange(
    new WorkItem { Title = "Update the README", Status = WorkItemStatus.Todo, UpdatedAt = DateTimeOffset.UtcNow },
    new WorkItem { Title = "Triage issues", Status = WorkItemStatus.Todo, UpdatedAt = DateTimeOffset.UtcNow });
await db.SaveChangesAsync();

// ExecuteUpdate bypasses SaveChanges (and the interceptor) entirely - Trigger mode still catches it.
var closed = await db.WorkItems
    .Where(w => w.Status == WorkItemStatus.Todo)
    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WorkItemStatus.Cancelled));
Console.WriteLine();
Console.WriteLine($"ExecuteUpdate cancelled {closed} Todo item(s) directly in SQL - no SaveChanges call.");
#endregion bulk-write

#region bulk-write-history
Console.WriteLine("History<WorkItem>() for those rows - the trigger still recorded it, with no change context:");
await foreach (var v in db.History<WorkItem>()
    .Where(v => v.Entity.Status == WorkItemStatus.Cancelled)
    .AsAsyncEnumerable())
{
    Console.WriteLine($"  {v.Entity.Title,-22} changed_by: {v.ChangedBy ?? "NULL"}, reason: {v.Reason ?? "NULL"}");
}
#endregion bulk-write-history
