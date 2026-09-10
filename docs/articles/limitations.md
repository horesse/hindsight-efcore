# Limitations and non-goals

## Not in v1

- **Bitemporal** (system time + application time). PostgreSQL 19's `FOR PORTION OF` covers the
  application-time half; combining them is a v2 topic. The API reserves `AsOfValid(...)` for it.
- **`AsOf()` with `Include()`** — throws `NotSupportedException` (DESIGN.md D8). Load related rows
  with a second `AsOf()` query.
- **`AsOf()` not first in the query** — `db.Set<T>().Where(...).AsOf(t)` throws; `AsOf` must be the
  first operator, on the `DbSet` itself.
- **`AsOf()` on owned / complex / inherited entities** — throws `NotSupportedException`: those column
  sets are not reconstructable from the history table in v1. Use `FromSql` against the history table.
- **`AllVersions()` / `History<T>()`** — not implemented yet; only `AsOf()` reads history so far.
- **Restoring** an entity to a previous version — history is read-only. An `AsOf()` snapshot is
  detached and no-tracking; re-attaching one and calling `SaveChanges` is not currently blocked, so
  treat historical results as read-only.
- **TPH hierarchies** — rejected at model validation.
- **Owned collections** inside temporal entities — owned references and complex properties are
  supported for writing history (their columns live in the same table), owned collections are not.
- **Providers other than Npgsql** — none, by design. Provider-neutral abstractions built "for later"
  are always wrong later.

## Known trade-offs

- `HistoryWriter.Interceptor` cannot see `ExecuteUpdate`, `ExecuteDelete`, raw SQL, or writes from other
  processes. `HistoryWriter.Trigger` will cover that; it is not implemented yet, so today this is a
  hard limitation, not a choice.
- The change-context columns (`changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`)
  are written `NULL` unless an `IChangeContextProvider` is registered
  (`UseHindsight(h => h.WithChangeContext<T>())`). Hindsight ships no default provider.
- History tables grow without bound. Partitioning by `valid_from` and retention are v2; the schema is
  chosen so they can be added without migration of existing data.
