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
- `HistoryWriter.Interceptor` does not see `ExecuteUpdate` / `ExecuteDelete` / raw SQL (DESIGN.md D4),
  and the change-context columns (`changed_by`, `correlation_id`, `reason`, …) are written `NULL`
  until the change-context provider ships.
