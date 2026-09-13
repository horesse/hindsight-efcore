---
order: 10
---

# Choosing a history writer

A *history writer* turns every insert, update and delete of a temporal entity into rows in its history
table. Hindsight has two. Both produce the same history table and the same half-open periods, so you
can switch between them with one migration.

::: tip Recommendation: `HistoryWriter.Trigger`
Use the trigger writer in production. It records every change, including `ExecuteUpdate`,
`ExecuteDelete`, raw SQL and other processes, and it has no clock-skew exposure. Use the interceptor
writer only if the role that applies migrations cannot create functions and triggers.
:::

<<< @/snippets/GettingStarted.cs#register

`UseHindsight()` without `UseHistoryWriter(...)` uses `HistoryWriter.Interceptor`.

## Side by side

| | [`HistoryWriter.Trigger`](/writing/trigger) | [`HistoryWriter.Interceptor`](/writing/interceptor) |
|---|---|---|
| Where history is written | a plpgsql trigger that the migration creates | a `SaveChangesInterceptor` in the application |
| Records `ExecuteUpdate`, `ExecuteDelete`, raw SQL, other processes | ✅ (with `NULL` change context) | ❌ |
| Same transaction as the change | ✅ by construction | ✅ |
| Timestamp | `now()`, one per transaction | `TimeProvider`, one per `SaveChanges` |
| Concurrent writers, clock skew | safe | safe, but a slower clock's version is dated by the faster one |
| An update that sets a column to its current value | writes nothing | writes a version |
| Extra cost of a delete | none | one `SELECT … FOR UPDATE` per `SaveChanges` |
| Can fail after EF Core accepted the changes | no | yes, [handled](/writing/interceptor#if-the-history-write-fails) |
| Needs | ownership of the table and `CREATE` on the schema | nothing beyond writing the tables |
| Overhead on a 100-row `SaveChanges` | +2 to +6 ms | +7 to +16 ms |

The overhead figures are from the [benchmarks](/reference/benchmarks); your numbers will differ, but
the ratio holds.

## Differences you can observe

Code that runs under both writers can tell them apart in two ways:

- **Timestamps.** The interceptor uses your `TimeProvider`, so a test can inject time. The trigger
  uses `now()`.
- **No-op updates.** If EF Core marks a property as modified but its value did not change, the
  interceptor writes a version and the trigger does not, because the trigger compares the old and new
  row.

## Switching writers

Change the option and add a migration:

```bash
dotnet ef migrations add SwitchToTriggerWriter
```

The migration creates the trigger function and trigger (or, when switching back, drops them). It never
touches your data or the history table's columns.
