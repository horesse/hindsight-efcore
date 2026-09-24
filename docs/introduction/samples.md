---
order: 25
---

# Samples

Two runnable console apps, each a self-contained tour of one side of Hindsight, and a small web page
that puts them together into a history screen. None needs a database of your own - each starts and
tears down its own disposable PostgreSQL 17 container, so `dotnet run` is the whole setup.

| | demonstrates | writer |
|---|---|---|
| [`ProductCatalogSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/ProductCatalogSample) | `AsOf`, `AllVersions`, `History<T>`, delete tombstones | `HistoryWriter.Interceptor` (the default) |
| [`TaskTrackerSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/TaskTrackerSample) | who changed a row and why, and what a raw `ExecuteUpdate` leaves behind | `HistoryWriter.Trigger` |
| [`HistoryViewerSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/HistoryViewerSample) | a read-only history screen: timeline, `Diff` between versions, `AsOf` at any instant | `HistoryWriter.Trigger` |

The [tutorial](/tutorials/audit-trail) walks through the third sample project,
[`InsuranceSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/InsuranceSample),
step by step against an ASP.NET Core API; these are shorter and meant to just be run.

## Product catalog

```bash
dotnet run --project samples/ProductCatalogSample
```

A product's price changes twice after it's created. `AsOf` answers what it looked like at an earlier
point in time, with no extra code at the call site:

<<< @/../samples/ProductCatalogSample/Program.cs#as-of

`History<Product>()` shows the same timeline with its validity windows and what changed:

<<< @/../samples/ProductCatalogSample/Program.cs#history

The product is then deleted. It's gone from `products` - but `History<Product>()` still has the
tombstone: when it was deleted and what it looked like right before.

## Task tracker

```bash
dotnet run --project samples/TaskTrackerSample
```

Two simulated users move a work item through its statuses. Each change is wrapped in
`DbContext.WithReason(...)`, so the audit trail carries who made it and why:

<<< @/../samples/TaskTrackerSample/Program.cs#status-changes

`History<WorkItem>()` reads that trail back:

<<< @/../samples/TaskTrackerSample/Program.cs#audit-trail

Then a plain `ExecuteUpdateAsync` moves a batch of items directly in SQL - no `SaveChanges`, no
interceptor anywhere in the call stack. `HistoryWriter.Trigger` catches it anyway, with `NULL` in every
change-context column, since there's no `SaveChanges` call for a provider to attach one to (see
[Choosing a history writer](/writing/history-writers#side-by-side)):

<<< @/../samples/TaskTrackerSample/Program.cs#bulk-write

## History viewer

```bash
dotnet run --project samples/HistoryViewerSample
```

A read-only web page over the product catalog, showing what an audit or "history" screen built on
Hindsight looks like. It seeds three products with several versions each (price changes, a rename, a
delete, three users, a reason on every change), then opens `http://localhost:5080` in your browser. It
uses `HistoryWriter.Trigger`, so the timeline also shows a bulk `ExecuteUpdate`.

![The timeline of one product: who changed it, when, why and what changed, with the version in effect at the chosen instant highlighted](/images/samples/history-viewer.png)

The product list comes from `History<Product>()`, so deleted products stay on it. The newest row per
product is its latest state, or its delete tombstone:

<<< @/../samples/HistoryViewerSample/HistoryQueries.cs#product-list

A product's timeline is its `History<Product>()` rows with who, when and why, and each version
[diffed](/querying/diff) against the one before it. The delete tombstone has no state to compare, so
the loop skips it:

<<< @/../samples/HistoryViewerSample/HistoryQueries.cs#timeline

The row that `ExecuteUpdate` wrote has no change context: no `SaveChanges` ran, so there was nothing to
attach one to, and the page says so instead of showing an empty name. Picking an instant shows the
product as it was then, with [`AsOf`](/querying/as-of):

<<< @/../samples/HistoryViewerSample/HistoryQueries.cs#as-of

The page has no edit or restore button: history is [read-only](/querying/restrictions#history-is-read-only).

## Next steps

- [Concepts](/introduction/concepts): versions, periods and tombstones, in five minutes.
- [Choosing a history writer](/writing/history-writers): Interceptor vs. Trigger, in depth.
- [Change context](/writing/change-context): store the user, the correlation id and the reason with
  every version.
