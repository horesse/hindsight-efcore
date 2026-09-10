# Limitations and non-goals

## Not in v1

- **Bitemporal** (system time + application time). PostgreSQL 19's `FOR PORTION OF` covers the
  application-time half; combining them is a v2 topic. The API reserves `AsOfValid(...)` for it.
- **`AsOf()` with `Include()`** — throws `NotSupportedException` (DESIGN.md D8). Load related rows
  with a second `AsOf()` query.
- **`AsOf()` not first in the query** — `db.Set<T>().Where(...).AsOf(t)` throws; `AsOf` must be the
  first operator, on the `DbSet` itself.
- **`AsOf()` / `AllVersions()` / `History<T>()` on owned / complex / inherited entities** — throws
  `NotSupportedException`: those column sets are not reconstructable from the history table in v1. Use
  `FromSql` against the history table.
- **Restoring** an entity to a previous version — history is read-only (DESIGN.md D7). An `AsOf()` /
  `AllVersions()` / `History<T>()` snapshot is detached and no-tracking, and re-attaching one
  (`Update` / `Attach` / `Add` / `Remove`) and calling `SaveChanges` throws
  `InvalidOperationException` rather than write the stale snapshot back as the current version. To
  roll a value back, copy it from the snapshot onto a fresh instance, or onto one loaded with a
  normal query / `DbSet.Find()`, and save that.
- **TPH hierarchies** — rejected at model validation.
- **Removing a primary-key property from a temporal entity** — rejected at model finalization with
  `InvalidOperationException`. History keeps every other removed column (as a nullable orphan), but
  the version index and the writer's close-previous-version step are keyed on the primary-key
  columns, so those cannot be dropped while the entity stays temporal.
- **Owned collections** inside temporal entities — owned references and complex properties are
  supported for writing history (their columns live in the same table), owned collections are not.
- **Providers other than Npgsql** — none, by design. Provider-neutral abstractions built "for later"
  are always wrong later.

## Known trade-offs

- `HistoryWriter.Interceptor` cannot see `ExecuteUpdate`, `ExecuteDelete`, raw SQL, or writes from
  other processes — they write no history. `HistoryWriter.Trigger` covers all of them (the trigger is
  in the database); switch to it if that matters. Under the trigger writer those paths still record
  history but with `NULL` change-context columns, since no `ChangeContext` is pushed for a write that
  does not go through `SaveChanges`.
- The two writers differ in two observable ways: the interceptor timestamps from `TimeProvider` (so
  tests can inject time) while the trigger uses `now()`; and an `UPDATE` that assigns a versioned
  column its current value writes a history row under the interceptor but not under the trigger.
- The change-context columns (`changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`)
  are written `NULL` unless an `IChangeContextProvider` is registered
  (`UseHindsight(h => h.WithChangeContext<T>())`). Hindsight ships no default provider.
- History tables grow without bound. Partitioning by `valid_from` and retention are v2; the schema is
  chosen so they can be added without migration of existing data.
