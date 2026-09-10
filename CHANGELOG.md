# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added

- Repository scaffold: solution, packaging, CI, release workflow.
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
  default and only implemented writer is `HistoryWriter.Interceptor`; `HistoryWriter.Trigger` throws
  `NotSupportedException` until a later release.
- `HistoryWriter.Interceptor` does not see `ExecuteUpdate` / `ExecuteDelete` / raw SQL (DESIGN.md D4).
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
