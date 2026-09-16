---
order: 25
---

# Samples

Two runnable console apps, each a self-contained tour of one side of Hindsight. Neither needs a
database of your own - both start and tear down their own disposable PostgreSQL 17 container, so
`dotnet run` is the whole setup.

| | demonstrates | writer |
|---|---|---|
| [`ProductCatalogSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/ProductCatalogSample) | `AsOf`, `AllVersions`, `History<T>`, delete tombstones | `HistoryWriter.Interceptor` (the default) |
| [`TaskTrackerSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/TaskTrackerSample) | who changed a row and why, and what a raw `ExecuteUpdate` leaves behind | `HistoryWriter.Trigger` |

The [tutorial](/tutorials/audit-trail) walks through the third sample project,
[`InsuranceSample`](https://github.com/horesse/hindsight-efcore/tree/master/samples/InsuranceSample),
step by step against an ASP.NET Core API; these two are shorter and meant to just be run.

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

## Next steps

- [Concepts](/introduction/concepts): versions, periods and tombstones, in five minutes.
- [Choosing a history writer](/writing/history-writers): Interceptor vs. Trigger, in depth.
- [Change context](/writing/change-context): store the user, the correlation id and the reason with
  every version.
