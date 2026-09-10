# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added

- Repository scaffold: solution, packaging, CI, release workflow.
- Benchmark numbers comparing `HistoryWriter.Interceptor` and `HistoryWriter.Trigger` against plain
  EF Core — `SaveChanges` insert / update / delete overhead and the cost of attaching a change
  context — in `README.md` and the History writers guide. Run with
  `dotnet run -c Release --project benchmarks/Hindsight.Benchmarks -- --filter '*'` (needs Docker).
- `IsTemporal()` fluent configuration writing `Hindsight:*` annotations.
- `UseHindsight()` on `DbContextOptionsBuilder`: enables the model-finalizing convention that
  generates a property-bag history entity type for every `IsTemporal()` entity, so
  `dotnet ef migrations add` creates the history table alongside the main table. The history table
  carries the versioned columns (excluded properties omitted, every column nullable), the period
  columns `valid_from` / `valid_to`, the change-context columns
  (`operation`, `changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`), an identity
  `history_id` primary key, and a `(key…, valid_from desc)` index.
- Model validation: a temporal entity without a primary key throws `InvalidOperationException`; a
  temporal entity in a TPH hierarchy throws `NotSupportedException`.
- History is now written: inserts, updates and deletes of a temporal entity produce history rows in
  the same transaction as the data change, with half-open `[valid_from, valid_to)` intervals and one
  timestamp per `SaveChanges` taken from the registered `TimeProvider`. A delete closes the open
  version and records a tombstone row (`operation = 3`, empty interval), so a deleted entity has no
  open history row. A `SaveChanges` that touched only excluded properties writes nothing.
- `HistoryWriter` enum and `UseHindsight(h => h.UseHistoryWriter(...))` to choose the writer. The
  default is `HistoryWriter.Interceptor`.
- `HistoryWriter.Trigger` is now implemented and recommended in production. Selecting it makes the
  migration generate a `<history_table>_write()` plpgsql function and an
  `AFTER INSERT OR UPDATE OR DELETE` `<history_table>_trg` trigger on the main table, from the same
  `Hindsight:*` annotations the history table is built from. It produces the same history schema and
  the same half-open intervals as the interceptor, uses `now()` for the timestamp (one per
  transaction), closes the previous version with
  `GREATEST(now(), valid_from + interval '1 microsecond')` so racing transactions stay contiguous, and
  needs only table ownership — no superuser, no extension. Because it lives in the database it also
  records history for `ExecuteUpdate` / `ExecuteDelete` and raw SQL (DESIGN.md D4). A later migration
  that adds, drops or renames a column on a temporal entity re-emits `CREATE OR REPLACE FUNCTION`; no
  migration ever drops the function's history table. Under the trigger writer, an `UPDATE` that sets a
  versioned column to its current value writes no history row.
- `HistoryWriter.Interceptor` does not see `ExecuteUpdate` / `ExecuteDelete` / raw SQL (DESIGN.md D4);
  `HistoryWriter.Trigger` does.
- Change context under `HistoryWriter.Trigger`: the registered `IChangeContextProvider` is called once
  per `SaveChanges` that writes a temporal entity, and its `ChangeContext` is pushed into the
  transaction with `set_config('hindsight.<key>', value, true)` for the trigger to read back with
  `current_setting(..., true)`. Hindsight opens a transaction for that `SaveChanges` if the caller has
  none. `DbContext.WithReason("…")` works the same as in interceptor mode. Bulk writes get history
  rows with `NULL` context columns.
- Change context: `IChangeContextProvider` and `UseHindsight(h => h.WithChangeContext<T>())`. When a
  provider is registered, `HistoryWriter.Interceptor` calls it once per `SaveChanges` and writes the
  returned `ChangeContext` (`UserId`, `UserName`, `CorrelationId`, `Reason`, `Extra` JSON) into the
  `changed_by`, `changed_by_name`, `correlation_id`, `reason` and `extra` columns of that call's
  history rows. Without a provider those columns stay `NULL` and `SaveChanges` still succeeds.
  `DbContext.WithReason("…")` opens a scope that overrides the reason for the `SaveChanges` calls
  inside it. Hindsight ships no default provider.
- `queryable.AsOf(DateTimeOffset)`: reads each temporal entity as it stood at a point in time from
  its history table. `AsOf` must be the first operator on the query; `Where`, `OrderBy`, `Select`,
  `First`, `Any`, `Count` compose after it and translate to a single SQL query with the instant as a
  parameter and the half-open predicate `valid_from <= @asOf AND valid_to > @asOf`. Results are
  always no-tracking; `AsOf(...).AsTracking()` throws. `AsOf` on a non-temporal entity throws
  `InvalidOperationException`; `AsOf` with `Include` / `ThenInclude` throws `NotSupportedException`
  (DESIGN.md D8); `AsOf` on an inheritance hierarchy or an entity with owned/complex members throws
  `NotSupportedException`.
- `queryable.AllVersions()`: reads every stored version of a temporal entity from its history table —
  one row per insert and update, newest first (by `valid_from` descending, replaceable with your own
  `OrderBy`). The `delete` tombstone is excluded, so a deleted entity's timeline ends at the version
  that was open when it was deleted. Same rules as `AsOf`: first operator on the query, composes into
  a single SQL query, always no-tracking, and the same `InvalidOperationException` /
  `NotSupportedException` guards for non-temporal entities, `Include`, `AsTracking`, inheritance and
  owned/complex members.
- `db.History<T>()`: reads every stored version of a temporal entity as a `Version<T>` — the entity
  snapshot plus the version's system-time period (`ValidFrom` / `ValidTo` as `DateTimeOffset`,
  `IsCurrent` for the open one) and the change-context columns (`Operation`, `ChangedBy`,
  `ChangedByName`, `CorrelationId`, `Reason`, `Extra`). Unlike `AllVersions()` it **includes the
  `delete` tombstone** as `Version` with `Operation == VersionOperation.Delete` and an empty interval,
  so it is the delete audit. Newest first (by `valid_from` descending, then `history_id`), replaceable
  with your own `OrderBy`. `Where` and `Select` compose into a single SQL query through both the
  metadata members and the `Entity` snapshot (`Where(v => v.Entity.Id == id)`), always no-tracking.
  Same `InvalidOperationException` / `NotSupportedException` guards as `AsOf` / `AllVersions` for
  non-temporal entities, `Include`, `AsTracking`, inheritance and owned/complex members. New public
  `Version<TEntity>` and `VersionOperation` types.
- History results are read-only, enforced (DESIGN.md D7). Re-attaching an entity that came from
  `AsOf()` / `AllVersions()` / `History<T>()` (`Update`, `Attach`, `Add`, `Remove`, or a manual state
  change) and calling `SaveChanges` throws `InvalidOperationException` instead of silently writing the
  stale snapshot back as the current version. `Version<T>.Entity` is covered too. Copying values onto
  a fresh instance, or onto one from `DbSet.Find()` / a normal query, is unaffected. No new public
  API.
- Removing a property from a temporal entity now keeps its history column (DESIGN.md D6, golden
  rule 3). `dotnet ef migrations add` drops the column from the main table only; on the history table
  the column stays, forced nullable, and is tagged `Hindsight:Orphaned` in the model. The convention
  learns which columns to keep by reading the previous `ModelSnapshot`, so no migration ever emits a
  `DropColumn` / `DropTable` / narrowing `AlterColumn` on a history table. Removing a **primary-key**
  property from a temporal entity throws `InvalidOperationException` — the history version index and
  the writer's close-previous-version step need the key columns. No new public API.
