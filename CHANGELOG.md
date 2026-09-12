# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Changed

- Documented the change-context trust model: `docs/articles/configuration.md` and
  `docs/articles/history-writers.md` now state plainly that `changed_by` / `changed_by_name` /
  `correlation_id` / `reason` / `extra` are application-asserted, not database-guaranteed — under
  `HistoryWriter.Trigger` they round-trip through an unauthenticated `set_config`/`current_setting`
  session setting that anything with an ordinary database connection can also set. This was always the
  design (`DESIGN.md` D3) but was not written down anywhere a reader could find it. `README.md`'s
  comparison table gets a footnote on the same row. No code changed; see `DESIGN.md` D15 for the
  open question this raised about an additional, database-guaranteed `session_user` column.

### Added

- `UseHindsight()` now validates that the context is configured with the Npgsql/PostgreSQL provider,
  the only one Hindsight supports (see
  [Limitations → Not in v1](docs/articles/limitations.md#not-in-v1)). The check runs the first time
  the context is used, so `UseSqlite(...).UseHindsight()` (or any other provider) now fails fast with
  a clear `InvalidOperationException` naming the provider found and the one required, instead of
  surfacing a confusing DI-resolution or SQL-generation error later from `EnsureCreated`,
  `dotnet ef migrations add`, or `SaveChanges`. No new public API.
- Every history table now also gets a `gist (tstzrange(valid_from, valid_to))` period-range index
  (`ix_<history_table>_period`), created by the migration alongside the table in both
  `HistoryWriter.Interceptor` and `HistoryWriter.Trigger` mode. `DESIGN.md` and
  `.claude/rules/sql-and-migrations.md` had documented this index since before any code existed; it
  had never actually been built. Without it, an `AsOf` / `History<T>` query that does not also filter
  on the entity's primary key forced a sequential scan of the whole history table for the
  period-overlap predicate. No new PostgreSQL extension is required — range types have a native GiST
  operator class in PostgreSQL core. No new public API.
- A temporal entity whose history table name (default or `UseHistoryTable(...)`) would produce a
  generated table, trigger function, trigger, or index name over 63 bytes now fails fast at model-build
  time with a clear `InvalidOperationException`, instead of silently colliding with another entity's
  identifier once PostgreSQL truncates it (`NAMEDATALEN - 1`) — a collision that fails loudly for a
  table or index but is a *silent* history corruption for the trigger function, which PostgreSQL simply
  replaces with no error. See [Model validation](docs/articles/configuration.md#model-validation). No
  new public API.

### Fixed

- `AsOf()`, `AllVersions()` and `History<T>()` now throw `NotSupportedException` (DESIGN.md D7) when
  combined with `ExecuteUpdate()` / `ExecuteUpdateAsync()` / `ExecuteDelete()` / `ExecuteDeleteAsync()`.
  This closes a real data-corruption path, not just a documentation gap: EF Core's own `ExecuteUpdate`
  translator resolved the rewritten query straight to a live `UPDATE` against the *history table*
  (`policies_history`, not the main table) with no error at all — confirmed against real PostgreSQL for
  all three markers. `ExecuteDelete` happened to be rejected by EF Core's own translator already, but
  Hindsight no longer relies on that accident either; all four now fail before any SQL is generated.
- Both writers now cooperate with an ambient `System.Transactions.TransactionScope` instead of failing
  inside it. Previously, `SaveChanges` called with no `context.Database.CurrentTransaction` open always
  tried to start one of its own — including inside a caller's `TransactionScope` that had already
  enlisted the connection — which EF Core rejected with
  `InvalidOperationException: An ambient transaction has been detected...`, in both writer modes,
  regardless of `EnableRetryOnFailure()`. Hindsight now checks for
  `System.Transactions.Transaction.Current` first and, when one is present, opens no transaction of its
  own: it opens the connection explicitly instead, so Npgsql enlists it in the ambient transaction, and
  lets that transaction own commit/rollback for the data change and the history row(s) together, exactly
  as a transaction Hindsight opens itself would. See
  [Configuration → Ambient TransactionScope](docs/articles/configuration.md#ambient-transactionscope).
  (A retrying execution strategy still cannot be combined with an ambient `TransactionScope` — that is
  an independent, EF-Core-native restriction, unchanged by this fix; see
  [Configuration → EnableRetryOnFailure and transactions](docs/articles/configuration.md#enableretryonfailure-and-transactions).)
- `HistoryWriter.Interceptor` no longer writes a fabricated delete tombstone when an entity is removed
  without being loaded first (`Remove(new Policy { Id = id })`, or `Attach` then `Remove`). It
  previously trusted `EntityEntry.OriginalValues`, which for a never-loaded stub is just the CLR
  defaults the caller's instance happened to hold, not the database's last known values — so the
  tombstone silently carried garbage (all-default/null columns) instead of the entity's real
  pre-delete state that `DESIGN.md` D5/D12 promise. `SaveChanges` now re-reads each deleted entity's
  row from the database by primary key (`SELECT … FOR UPDATE`, in the same transaction as the delete)
  before writing its tombstone; if no row matches, it throws `InvalidOperationException` instead of
  writing an empty or fabricated one. One extra round trip per `SaveChanges` that deletes a temporal
  entity — see [Limitations → Known trade-offs](docs/articles/limitations.md#known-trade-offs).
  `HistoryWriter.Trigger` was never affected (it reads `OLD.*` from PostgreSQL directly).
- Model validation: `IsTemporal()` now throws `InvalidOperationException` at model-build time when
  every property of a temporal entity's primary key is excluded from history with `Exclude(...)`,
  naming the entity. Previously this built a model that silently wrote no history row for any insert,
  update or delete on the entity, forever, with no error — the writer had no key column left to find
  "the previous version" to close. Excluding some (not all) properties of a composite key is
  unaffected and still allowed.
- Model validation: a temporal entity property whose column collides with a fixed history column
  (`history_id`, `operation`, `changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`) or
  with its own period-start/period-end column now throws `InvalidOperationException` at model-build
  time, naming the entity, the property and the column. Previously the collision went undetected at
  build time and surfaced later as a raw PostgreSQL error on the first `SaveChanges`
  (`HistoryWriter.Interceptor`) or as the entity's own value for that column being silently dropped
  forever with no error at all (`HistoryWriter.Trigger`). Equal period-start/period-end column names
  are rejected the same way.
- `IsTemporal()` now throws `NotSupportedException` for an entity with an owned reference (`OwnsOne`)
  or a complex property, instead of silently building a history table that is missing their columns —
  a `SaveChanges` that changed only one of them could previously write zero history rows.
- `HistoryWriter.Interceptor` no longer produces overlapping or negative history intervals when two
  transactions update the same row concurrently, or when the clock steps backwards between two
  `SaveChanges`. The previous version is now closed with
  `GREATEST(@ts, valid_from + interval '1 microsecond')` and the new version starts at exactly that
  value — the same clamp `HistoryWriter.Trigger` already applied — so `AsOf` at any instant returns
  at most one row per entity. With a monotonic clock the written timestamps are unchanged. The close
  and the insert are now one statement per row (a data-modifying CTE); batching is unaffected.
- `SaveChanges` on a context configured with `UseNpgsql(cs, o => o.EnableRetryOnFailure())` now throws
  a clear `InvalidOperationException` naming the conflict and the fix, instead of a confusing
  EF Core- or Npgsql-authored exception that never mentions Hindsight, whenever a history writer would
  otherwise open its own transaction (no ambient transaction from the caller). Wrap the call in
  `CreateExecutionStrategy().Execute(...)`/`ExecuteAsync(...)` with your own transaction to use retry
  with Hindsight. See [Configuration → EnableRetryOnFailure and transactions](docs/articles/configuration.md#enableretryonfailure-and-transactions).
- `HistoryWriter.Interceptor`: when the history write itself fails after a successful data write (for
  example, invalid `ChangeContext.Extra` JSON, or a transient connection failure between the data write
  and the history write), the transaction still rolls back both writes together as before, but `Added`
  and `Modified` entities are now restored to their pre-save `EntityState` before the exception is
  rethrown. Previously EF Core's own `SaveChanges` pipeline had already called
  `ChangeTracker.AcceptAllChanges()` before Hindsight's `SavedChanges` interceptor ran, so a caller
  catching the exception and retrying `SaveChanges()` on the same context found nothing left to save
  and silently did nothing, even though the database held none of the failed change. A `Deleted`
  entity's `EntityEntry` is already detached by `AcceptAllChanges()` by that point and cannot be
  restored the same way; this remains a documented caveat — see
  [Limitations → Known trade-offs](docs/articles/limitations.md#known-trade-offs) and
  [History writers → What happens if the history write fails](docs/articles/history-writers.md#what-happens-if-the-history-write-fails).
- The save-back guard (DESIGN.md D7) now also catches an `AsOf` / `AllVersions` / `History<T>()`
  result reached through the *second* argument of `Concat` / `Union` / `Except` / `Intersect` (for
  example `otherQuery.Concat(db.Policies.AsOf(at))`), not just the first. The internal tagging walk
  only ever followed the first argument of each method call in the chain, so a history-derived
  instance surfacing through that shape was never marked, and re-attaching it with `Update()` +
  `SaveChanges()` silently wrote the stale snapshot back as the current version instead of throwing.
- `DbContext.WithReason("…")` now scopes its override to the specific `DbContext` instance it was
  called on, in both `HistoryWriter.Interceptor` and `HistoryWriter.Trigger` mode. It previously set
  an `AsyncLocal` shared by the whole asynchronous control flow, so a `SaveChanges` on a *different*
  `DbContext` running inside the same `using (dbA.WithReason(...))` block — a second, unrelated
  context saved in the same method, the same request, or the same background job — silently picked up
  `dbA`'s reason instead of its own (or `null`). The method signature is unchanged; nesting on the
  same context still restores the previous value innermost-first on dispose.
- A failure to resolve a `Scoped`-registered `IChangeContextProvider` from the application service
  provider now throws a Hindsight-specific `InvalidOperationException` naming the provider type and
  pointing at [Configuration → Pooled and factory-created
  contexts](docs/articles/configuration.md#pooled-and-factory-created-contexts), with the original
  DI-resolution error preserved as `InnerException`, instead of forwarding ASP.NET Core's generic
  "Cannot resolve scoped service '...' from root provider." on its own. This surfaces under
  `AddDbContextPool<T>()` / `AddDbContextFactory<T>()` with `ServiceProviderOptions.ValidateScopes` on
  (ASP.NET Core's Development default); with it off (the common Production default), no exception is
  thrown at all — the provider silently becomes a captive singleton instead, which the new
  documentation section covers, since it cannot be reliably detected at runtime. The resolution logic
  itself, previously duplicated between `HistoryWriter.Interceptor` and `HistoryWriter.Trigger`, is now
  one internal `ChangeContextProviderResolver` shared by both. No public API change.

### Changed

- Documentation: the recommendation to use `HistoryWriter.Trigger` in production is now called out
  right after the `UseHindsight(...)` configuration sample in `README.md`, and at the top of
  [History writers](docs/articles/history-writers.md), instead of only inside the writer comparison
  table further down each page. No behavior changed — `HistoryWriter.Interceptor` remains the default
  writer when `UseHistoryWriter(...)` is never called.
- `SaveChanges` no longer runs a redundant full `ChangeTracker.DetectChanges()` pass over every tracked
  entity — not just temporal ones — when `HistoryRowPlan.BuildPending` (`HistoryWriter.Interceptor`) or
  `HistoryTriggerContextInterceptor.HasTemporalChange` (`HistoryWriter.Trigger`) call
  `ChangeTracker.Entries()`. `HistorySnapshotGuardInterceptor.Guard` already walks `Entries()` earlier
  in the same `SaveChanges`, and with `AutoDetectChangesEnabled` on (the default) that call already ran
  the one `DetectChanges()` pass needed; nothing mutates a tracked entity's properties between the two
  calls. The second call now temporarily disables `AutoDetectChangesEnabled` around its own walk and
  reuses `Guard`'s result instead of re-scanning. No behavior change for correctness — only measured on
  a context that also tracks many untouched entities alongside the one that actually changed, where
  managed allocations drop by roughly a third (about 32% at 10,000 tracked-but-unchanged entities, both
  writer modes) — see [History writers → Tracked-but-unchanged entities](docs/articles/history-writers.md#tracked-but-unchanged-entities).

## [1.0.0] - 2026-09-11

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
- Removing `IsTemporal()` from an entity entirely — not just one property — now keeps its history
  table too (DESIGN.md D6, golden rule 3). `dotnet ef migrations add` never emits a `DropTable` for a
  history table just because its source stopped being temporal (or was removed from the model): the
  whole history entity type is cloned back from the previous `ModelSnapshot` — columns, key, indexes —
  and tagged `Hindsight:Orphaned` on the entity type itself, the same way a single removed column was
  already retained. `AsOf()` / `AllVersions()` / `History<T>()` against the now-non-temporal entity
  still throw `InvalidOperationException` as before; only the table's fate changes. No new public API.
- Removing `IsTemporal()` from an entity under `HistoryWriter.Trigger` now also drops the trigger on the
  main table. Previously the trigger — which lives on the main table, not the history table — kept
  firing on every insert/update/delete and kept writing into the now-orphaned history table, even though
  the entity was no longer temporal in the model. The migration that removes `IsTemporal()` now emits a
  `DROP FUNCTION ... CASCADE` for it (which also drops the dependent trigger); no data is deleted, only
  the write path stops. No new public API.

### Changed

- `HistoryWriter.Interceptor` now writes all history rows of a `SaveChanges` in a single database
  round-trip (a batched `DbBatch`, chunked at 512 rows) instead of one `UPDATE` + `INSERT` round-trip
  per row. Same history rows, same intervals; large batch saves are dramatically faster.
