# Design decisions

Living document. Each entry: decision, why, what would make us revisit it. When a spike confirms or
refutes something, edit the entry — don't append a correction below it.

## Terminology

- **System time** — when the row physically existed in the database. This is what Hindsight tracks.
- **Application (valid) time** — when the fact was true in the real world ("this tariff applies from March 1").
  PostgreSQL 19 supports it natively (`FOR PORTION OF`, `WITHOUT OVERLAPS`). Not in scope for v1.

## D1. Two tables, not one table with a "current" flag

Main table stays untouched: existing queries, indexes and FKs don't change. History is a separate
table with the same columns plus period and context columns.

## D2. History table is a property-bag entity type in the model

`SharedTypeEntity<Dictionary<string, object>>("policies_history")`, built by a model-finalizing
convention from the `Hindsight:*` annotations. It is a real entity type, so `IMigrationsModelDiffer`
sees it and migrations come for free.

Why not a "shadow entity type": no such concept in EF Core — anything the differ doesn't see needs
custom `MigrationOperation`s wired into internals that break on minor releases.
Why not a shared-type entity type with the same CLR type: EF Core forbids a CLR type being both a
shared and a non-shared entity type.

**Type fidelity — resolved by spike, 2026-09-10.** A property-bag column declared only by CLR type
(`IndexerProperty(source.ClrType, name)`) loses the source's store type: `enum HasConversion<string>()`
→ `integer`, `jsonb` → `text`, `numeric(18,4)` → `numeric`, `varchar(8)` → `text`. Arrays (`text[]`)
survive because Npgsql infers them from the CLR type. The convention therefore **mirrors** each
source property's facets onto the history property, using only public APIs:
`GetColumnType()`, `GetValueConverter()` / `GetProviderClrType()`, `GetMaxLength()`, `IsUnicode()`,
`GetPrecision()` / `GetScale()`. With mirroring the EF model and the PostgreSQL catalog match the main
table exactly for every case above, including a custom `ValueConverter`. These getters only return the
configured values once the source entity is fully configured, so the convention must run at/after
model finalization. Covered by `PropertyBagTypeFidelitySpike` in `Hindsight.IntegrationTests`.

Revisit if: a column kind still diverges after mirroring (ranges, composite/enum PG types,
`[Column(TypeName)]` on an owned type).

## D3. Two history writers, same contract

- `Interceptor`: a `SaveChangesInterceptor`. In `SavingChanges` it snapshots the tracked temporal
  entities (state + versioned column values + which non-excluded properties changed) and, if the
  caller has no transaction open, starts one so the data change and the history rows commit together
  (D3) — unless an ambient `System.Transactions.TransactionScope` is present, in which case starting a
  second, EF-managed transaction is exactly what EF Core refuses; `HistoryWriterTransaction` detects
  `System.Transactions.Transaction.Current` (public API) and opens the connection explicitly instead,
  so Npgsql enlists it in the ambient transaction and every command from here on — the data write, the
  history `INSERT`, the trigger writer's `set_config` push — rides that enlistment with no separate
  `DbTransaction`; commit/rollback is then the ambient scope's job (`docs/articles/configuration.md` →
  Ambient TransactionScope). In `SavedChanges` — after the batch, so store-generated keys are known — it re-reads current
  values for `Added`/`Modified` and writes the history rows on the same connection and transaction
  via parameterised SQL (`ISqlGenerationHelper` for identifiers, each history column's
  `RelationalTypeMapping` for values); it commits only the transaction it started itself. Cross-hook
  state is keyed by the `DbContext` instance, never a field. Timestamp from `TimeProvider`
  (`CoreOptionsExtension.ApplicationServiceProvider` → `TimeProvider` ?? `TimeProvider.System`), one
  per `SaveChanges`. A `Modified`/`Deleted` row is one statement: a data-modifying CTE closes the
  open version with `GREATEST(@ts, valid_from + interval '1 microsecond')` and the outer `INSERT`
  starts the new version at the value the CTE returned (falling back to `@ts` when there was no open
  version) — the same clamp the trigger applies. Guaranteed as a result: a version chain is always
  contiguous with strictly positive intervals and exactly one open version, whatever `@ts` a
  `SaveChanges` captured — including a transaction that captured `@ts` and then waited on the row
  lock while another one committed a newer version, and a clock that steps backwards between saves.
  Not guaranteed: that `valid_from` equals the captured `@ts` — when the clamp fires, the new version
  starts 1µs after the previous one, later than the instant the (behind) clock reported. Two app
  instances with skewed clocks therefore never overlap, but the version the slower clock writes is
  dated by the faster one; cross-instance ordering is only as good as the clocks. `ExecuteUpdate`/raw
  SQL bypass it. This is the default writer.
- `Trigger` (implemented 2026-09-11): `CREATE OR REPLACE FUNCTION <history_table>_write()` +
  `CREATE OR REPLACE TRIGGER <history_table>_trg AFTER INSERT OR UPDATE OR DELETE` on the main table,
  emitted into the migration from the same annotations by a decorator over the provider's
  `IMigrationsSqlGenerator` (D13). Timestamp `now()`; the previous version is closed with
  `GREATEST(now(), valid_from + interval '1 microsecond')` and that value is captured
  (`RETURNING … INTO`) so the next version starts exactly where it ended — contiguous even when two
  transactions race. An `UPDATE` whose versioned columns are `NOT DISTINCT FROM` the old row writes
  nothing (matches "only excluded → no row", and additionally skips no-op value writes — a deliberate,
  documented difference from Interceptor mode). Orphaned history columns (D6) are not written by the
  trigger: there is no `NEW`/`OLD` column for them. Needs only table ownership — no superuser, no
  extension.

Change context (user id, user name, correlation id, reason, extra) is supplied by the application
through an `IChangeContextProvider`, registered with `UseHindsight(h => h.WithChangeContext<T>())`.

- `Interceptor`: the provider is called **once per `SaveChanges`**, in `SavingChanges` alongside the
  timestamp, and its `ChangeContext` is captured into the cross-hook state (never a field). In
  `SavedChanges` the values go straight into the `INSERT` of each new history version — the "close
  previous version" `UPDATE` does not touch them, so an old row keeps the context it was written with.
  No provider registered → the columns are `NULL` and `SaveChanges` still succeeds; a provider
  exception propagates and the transaction rolls back. `DbContext.WithReason(string)` opens an
  `AsyncLocal` scope whose reason overrides `ChangeContext.Reason` for the enclosed `SaveChanges`
  calls. `ChangeContext.Extra` is a caller-serialised JSON string written through the `extra` column's
  `jsonb` type mapping — the package takes no serialisation dependency.
- `Trigger`: the same `ChangeContext` is captured once per `SaveChanges` that writes a temporal
  entity and pushed with `set_config('hindsight.<key>', value, true)` (transaction-local); the trigger
  reads it with `current_setting(..., true)`. This is done from an **`ISaveChangesInterceptor`**, not
  an `IDbTransactionInterceptor` as first sketched — the spike (2026-09-11) found that EF Core's
  default `AutoTransactionBehavior.WhenNeeded` runs a single-statement `SaveChanges` with no
  transaction at all, and a transaction-local setting is a no-op outside a transaction. So the
  interceptor also opens a transaction when the caller has none (mirroring the Interceptor writer) and
  commits it with the data, the trigger's rows and the pushed context together. One extra round-trip
  per `SaveChanges`; measured in the benchmarks. `ExecuteUpdate`/`ExecuteDelete` and raw SQL do not
  pass through it, so the trigger records their history with `NULL` context columns.

`GetChangeContext` is synchronous: it runs inside both `SaveChanges` and `SaveChangesAsync`, and
blocking on an async source there would be sync-over-async. The provider type is resolved per
`SaveChanges` from `CoreOptionsExtension.ApplicationServiceProvider` (as `TimeProvider` is), falling
back to a parameterless constructor; no EF-internal service provider, no reflection on the per-row path.
This resolution is identical for both writers and lives once, in `ChangeContextProviderResolver`.

**`AddDbContextPool<T>()` / `AddDbContextFactory<T>()` and a `Scoped` provider — investigated
2026-09-12.** Confirmed empirically against EF Core 10.0.12: for these registration styles (pooled or
not), every context instance shares one `DbContextOptions` built once, so `ApplicationServiceProvider`
is whichever provider was active at that moment — normally the app's root container, never a request's
scope, unlike plain `AddDbContext<T>()`. Resolving a `Scoped` `IChangeContextProvider` from it either
throws (`ServiceProviderOptions.ValidateScopes` on — ASP.NET Core's Development default) or silently
returns a captive singleton carrying the first request's captured state forever (`ValidateScopes` off —
the common Production default). No registration pattern for the provider avoids this: the fixed point
is the captured `ApplicationServiceProvider` itself, not the provider's lifetime. Not detectable at
runtime either — `IServiceProvider.GetService` gives no public signal distinguishing a captive
singleton from a legitimately-already-constructed scoped instance, and `HindsightOptionsExtension.Validate`
runs before a service provider necessarily exists to inspect. Fixed the loud half only: the
`InvalidOperationException` from that resolution call is now wrapped with a Hindsight-specific message
naming the provider and the fix, instead of forwarding ASP.NET Core's generic one. The silent half is
a hard constraint, documented instead — see `docs/articles/configuration.md` → Pooled and
factory-created contexts for the safe pattern (a singleton provider reading per-request ambient state,
e.g. `IHttpContextAccessor`, fresh inside `GetChangeContext`).

The original design argument "a trigger can't know the user" is false — that's exactly what
`set_config` is for. Both writers implemented 2026-09-11. Trigger is the recommended mode; Interceptor
stays as a reference implementation and for environments where trigger creation is forbidden by policy.

## D4. Bulk operations are not intercepted

`ExecuteUpdate`/`ExecuteDelete` are caught by the Trigger writer for free (verified 2026-09-11 —
`TriggerHistoryWriterTests`): the trigger fires on the resulting rows and writes history, with `NULL`
change-context columns since those paths do not go through `SaveChanges`. In Interceptor mode they
bypass history; this is documented, not worked around. An analyzer diagnostic may come in v1.1.

## D5. History columns

| column | type | note |
|---|---|---|
| `history_id` | `bigint generated always as identity` | surrogate PK |
| *(entity columns)* | as in the main table | minus excluded properties |
| `valid_from` | `timestamptz not null` | |
| `valid_to` | `timestamptz not null` | `'infinity'` for the current version — cheaper in indexes than `null`, compares correctly |
| `operation` | `smallint` | 1 insert, 2 update, 3 delete |
| `changed_by` | `text null` | |
| `changed_by_name` | `text null` | |
| `correlation_id` | `text null` | |
| `reason` | `text null` | |
| `extra` | `jsonb null` | |

Indexes: `(pk columns, valid_from desc)` and GiST on `tstzrange(valid_from, valid_to)` (D14).
No FKs from history to the main table (parent may be deleted). All `not null` / unique / check
constraints of the original are dropped in history — a unique index on a history table is a reliable
way to break production on the second edit.

These column names, plus the entity's own configured period-start/period-end names, are reserved: a
source property whose column collides with one throws `InvalidOperationException` at model-build time
(`HistoryEntityTypeConvention.ValidateReservedColumnNames`). Without the check, `historyBuilder
.Property(fixedType, sameName)` in `AddContextColumns`/`AddPeriodColumns`/`AddSurrogateKey` silently
reconfigures the property `MirrorEntityColumns` already added under that name to the fixed CLR type,
so the collision is invisible at build time and only shows up later — as a duplicate-column or
identity-insert error from PostgreSQL in Interceptor mode, or as the entity's own value for that
column being silently dropped forever with no error at all in Trigger mode, since the migrations
generator excludes every fixed-name column from the trigger's versioned column list by name.

PostgreSQL truncates any identifier over 63 bytes (`NAMEDATALEN - 1`, UTF-8 bytes not characters)
silently, with no error — two identifiers that differ only after that point collide on the same
physical object. Hindsight generates several from the main table name: the history table itself
(`<table>_history`), the trigger function and trigger (`<history_table>_write`/`_trg`, Trigger mode
only), and two indexes (`ix_<history_table>_version`, `ix_<history_table>_period`, D14, both modes).
The tightest of these is the version index — `ix_` (3 bytes) + history table + `_version` (8 bytes),
on top of the history table's own `_history` (8 bytes), is 19 bytes of fixed overhead — so a main
table name of 44 ASCII bytes is the longest that keeps every generated identifier at or under 63
bytes; 45 already puts the version index one byte over. `HistoryEntityTypeConvention
.ValidateIdentifierLengths` computes every one of these identifiers for the entity's actual resolved
history table name (a `UseHistoryTable(...)` override if there is one, not just the default
derivation) and throws `InvalidOperationException` naming the offending identifier, its byte length,
and the fix, at the same model-build point as the other checks in this section. Checked
unconditionally rather than gated on `HistoryWriter.Trigger`, since a caller can switch writer modes
later with `UseHistoryWriter(...)` without rebuilding the model. Confirmed against real PostgreSQL
while investigating this bug: `CREATE TABLE`/`CREATE INDEX` on a collided name fails loudly ("relation
... already exists") the first time the migration is applied, but `CREATE OR REPLACE FUNCTION` does
not — it silently replaces the losing entity's trigger function body with the winning one's, and the
losing entity's own trigger (unaffected, since triggers are scoped per table, not global) goes on
calling the wrong function on every future write, corrupting that entity's history with no error
anywhere. `dotnet ef migrations add` itself catches none of this — EF Core has no concept of
PostgreSQL's identifier limit — so the collision would otherwise first surface as either of the above,
against a real database, arbitrarily long after the entities were defined.

Intervals are half-open `[valid_from, valid_to)`, everything is `timestamptz` in UTC.

**Row per operation.** `insert` closes nothing and writes one open row (`valid_to = 'infinity'`).
`update` closes the open row (`valid_to = @ts`) and writes a new open row. `delete` closes the open
row and writes a **tombstone**: `operation = 3`, `valid_from = valid_to = @ts` — an empty interval.
It never satisfies an `AsOf` predicate (`valid_from <= t AND valid_to > t`), so a deleted entity has
zero open versions, but the row still records *when* the delete happened and (once the change-context
provider ships) *who* did it, which a bare "close the last version" would lose and which the Trigger
writer's `AFTER DELETE` would record anyway. `AllVersions()` also excludes it (`operation <> 3`): it
carries the pre-delete column values but an empty interval, so it is a delete marker, not a state
version (D12). `History<T>()` is the one reader that returns it — that is where the *when* / *who* of
a delete is meant to be read (D12).

## D6. Schema evolution

- Column added to the entity → convention adds it to history → differ emits `AddColumn` for both.
- Column removed → **history keeps it, as a nullable orphaned column — resolved by spike, 2026-09-10.**
  The differ itself cannot be intercepted on public API (the only seam is a custom
  `IMigrationsModelDiffer`, whose interface's implementation namespace is
  `Microsoft.EntityFrameworkCore.Migrations.Internal` — golden rule 1). So the column is never allowed
  to disappear from the model in the first place. On every model build `HistoryEntityTypeConvention`
  resolves `IMigrationsAssembly.ModelSnapshot` (public API) and, for every property on the *previous*
  snapshot's history entity type that has no live source property behind it now, re-adds it to the
  current history entity type as a nullable shadow property tagged `Hindsight:Orphaned`, copying its
  store type / value converter / precision / scale / length straight from the snapshot property. The
  differ then sees no change (the column is still there and D5 already made it nullable), so it emits
  no `DropColumn` / `DropTable` / narrowing `AlterColumn` on a history table. Orphans accumulate
  across successive removals (each snapshot carries the previous ones); a re-run with no model change
  scaffolds an empty migration. Removing a **primary-key** property from a temporal entity throws
  `InvalidOperationException` — the history version index and the writer's close-previous-version step
  depend on the key columns. Building the snapshot model re-runs the finalizing conventions, so a
  thread-static guard stops the re-entrant pass from resolving the snapshot again. Nothing is
  intercepted, no differ subclass, no new public surface. Covered by `OrphanedHistoryColumnTests`
  (unit, incl. an `IMigrationsModelDiffer` assertion) and `HistoryColumnRetentionTests` (applies the
  generated DDL to real PostgreSQL and inspects `information_schema`).
- Column renamed → new column in history, old one stays as a nullable orphan (same mechanism). No
  rename magic.
- `IsTemporal()` removed from an entity entirely (or the entity type removed from the model
  altogether) → **the whole history table is kept, tagged orphaned — extended to whole entity types,
  2026-09-11.** The mechanism above only orphans a *column* whose source property disappeared from an
  entity that is still in `temporalEntityTypes`; it never ran for an entity that left that set
  altogether, because there was no history entity type left to attach an orphaned column to. So
  `HistoryEntityTypeConvention` also tracks which history entity type names it actually rebuilt this
  pass and, after that loop, walks the previous `ModelSnapshot` for every entity type tagged
  `Hindsight:IsHistoryTable` that isn't among them. Each one is re-materialized into the current
  model as a `SharedTypeEntity` cloned from the snapshot's own history entity type — every column with
  its store type / converter / length / precision / scale / default value / value-generation strategy,
  the primary key, every index — and the entity type itself is tagged `Hindsight:Orphaned` (the same
  annotation D6 already uses for a column, now also valid at entity-type scope). There is no live
  source entity left to mirror from at this point; the snapshot's history entity type, already fully
  built by this same convention when it was current, is the only source of truth. The differ then sees
  the table unchanged and emits nothing — no `CreateTable`, no `DropTable`, no `AlterColumn` — the same
  "the column/table never leaves the model" trick D6 already relies on for columns. Detection doesn't
  care *why* the entity type left `temporalEntityTypes` (`IsTemporal()` removed vs. the CLR type/DbSet
  removed vs. `Ignore()`d), only that it's no longer being rebuilt — one mechanism covers all three.
  Querying is unaffected: `AsOf()` / `AllVersions()` / `History<T>()` already throw
  `InvalidOperationException` on a non-temporal entity (the `Hindsight:HistoryEntityType` annotation
  that names its history table is only set while an entity is temporal), so an orphaned whole table is
  simply invisible to querying, same as before this fix — nothing new to guard there. Once orphaned, a
  history entity type is pinned forever the same way an orphaned column is: it reappears in every
  subsequent snapshot, so every later build re-clones it. Only a hand-written migration removes it,
  same carve-out as the rest of golden rule 3. Covered by
  `HistoryTableRetentionOnDetemporalizeTests` (Testcontainers: applies the generated DDL to real
  PostgreSQL and confirms the table and its data survive).
- Existing non-empty table made temporal → v1.1 (`INSERT ... SELECT` seeding the initial version).

## D7. Historical queries are always no-tracking

`AsOf`, `AllVersions` and `History<T>` return untracked results. Attempting to save an entity that
came from history — re-attaching one with `Update` / `Attach` / `Add` / `Remove` and calling
`SaveChanges` — throws `InvalidOperationException` with a clear message; see D12 for the mechanism.

**`ExecuteUpdate` / `ExecuteDelete` on a marked query — resolved by spike, 2026-09-12.** The
save-back guard above only covers `SaveChanges`; `ExecuteUpdate`/`ExecuteDelete` bypass the change
tracker entirely and translate the `IQueryable` directly into SQL, so that guard does nothing for
them. Verified against real PostgreSQL what EF Core 10 actually does with, e.g.,
`db.Policies.AsOf(t).ExecuteUpdateAsync(...)`: after `HistoryQueryRootRewriter` rewrites the marker to
`historyRoot.Where(...).Select(h => new Policy {...}).AsNoTracking()`, `ExecuteUpdate`'s translator
looks through the `Select` to the underlying table and emits a real `UPDATE ... policies_history ...`
— it does **not** throw, and it does **not** touch the main table; it silently mutates the audit
trail (confirmed for `AsOf()`, `AllVersions()`, and `History<T>()`, the last targeting a `Version<T>`
member such as `Reason`). `ExecuteDelete` happens to be rejected by EF Core's own translator today
("requires an entity type ... non-entity projection"), but that is an accident of the translator, not
a guarantee, and inconsistent with `ExecuteUpdate` on the identical query shape. So Hindsight rejects
all four (`ExecuteUpdate`/`ExecuteUpdateAsync`/`ExecuteDelete`/`ExecuteDeleteAsync`) itself, before any
SQL can be generated: `IQueryExpressionInterceptor.QueryCompilationStarting` **is** invoked for the
`ExecuteUpdate`/`ExecuteDelete` code path — confirmed by the same repro, since the tree it received
already carried the outer `ExecuteUpdate`/`ExecuteDelete` call — so no new interception point was
needed. `MarkerScanner` (`HindsightQueryExpressionInterceptor`) detects the same
`EntityFrameworkQueryableExtensions.ExecuteUpdate`/`ExecuteUpdateAsync`/`ExecuteDelete`/`ExecuteDeleteAsync`
methods anywhere in the tree, the same way it already detects `Include`/`AsTracking`, and throws
`NotSupportedException` naming this section. Covered by `AsOf_with_ExecuteUpdate_throws_...` /
`AllVersions_with_ExecuteDelete_throws_...` / `History_with_ExecuteUpdate_throws_...` (and their
"writes nothing" companions) in the integration test suite.

## D8. `AsOf` + `Include` throws in v1

It's an interval join on overlapping periods. A silently wrong answer is worse than no feature.

## D9. TPH hierarchies, owned references and complex properties are rejected in v1

One clear exception at model validation. Support is an issue, not a stretch goal.

**Owned references and complex properties — resolved 2026-09-11.** `HistoryEntityTypeConvention.MirrorEntityColumns`
iterates `source.GetProperties()` on the temporal entity's own `IConventionEntityType`; a property that
lives on an owned entity type (`OwnsOne`) or a complex type belongs to *that* type's own
`IConventionEntityType` / `IConventionComplexType` and is never returned by the owner's
`GetProperties()`, even though (for table splitting, the only mapping Hindsight or EF supports without
extra configuration) its column is physically present on the same table. Mirroring would therefore
silently drop those columns from history, and `HistoryRowPlan.HasVersionedModification` — which also
only looks at the top-level entry's properties — would silently write **zero** history rows for a
`SaveChanges` that touched only an owned/complex member (exactly the golden-rule-2 outcome this package
exists to prevent). `ValidateTemporalEntityType` therefore rejects it before any of that can happen:
`entityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned()) || entityType.GetComplexProperties().Any()`
throws `NotSupportedException` at `IsTemporal()` / model-finalization time, mirroring the identical guard
`HistoryQueryRootRewriter` already had on the read side (below) — now unreachable for any entity that
went through the convention, but left in place since it is the same defense-in-depth pattern as the TPH
check just above it, and a direct annotation-level bypass of the convention is the only way to reach it.
Owned collections were already out of scope (no columns on the owner's table at all) and stay so.
Revisit if a later version reconstructs owned/complex members on the read side (D12) *and* mirrors their
columns on write — both sides would need to move together, so partial support is not planned.

## D10. Naming

Package `Hindsight.EntityFrameworkCore.PostgreSQL`, namespace `Hindsight`. `Chrono` was taken;
`Tempora`/`Temporalis` collide with `Temporalio` (Temporal.io SDK) in search results.

## D11. Versioning and release

MinVer from git tags. A GitHub Release with tag `vX.Y.Z[-preview.N]` is the only path to nuget.org;
publishing uses nuget.org Trusted Publishing (OIDC) behind a reviewed `nuget` environment — no API key is stored.
Package validation (`EnablePackageValidation`) and PublicAPI analyzers guard the public surface.

**2.0 milestone tracking.** Whether `HistoryWriter.Trigger` becomes the default writer (see Open
questions) is a decision reserved for a deliberate 2.0 release — it needs a major version bump and its
own upgrade-guide migration note, not a patch. A GitHub milestone for 2.0 should carry this question so
it isn't lost between now and whenever that release is actually cut.

## D12. `AsOf` / `AllVersions` translate by query-root replacement — resolved by spike, 2026-09-10

`queryable.AsOf(at)` is a marker method. A public `IQueryExpressionInterceptor.QueryCompilationStarting`
hook (EF Core 10, no `*.Internal`) rewrites the source entity's query root into
`historyRoot.Where(valid_from <= @asOf && valid_to > @asOf).Select(h => new Policy { ... }).AsNoTracking()`
over the property-bag history entity type, found via the `Hindsight:HistoryEntityType` annotation.
The instant is a closure member access, not `Expression.Constant`, so EF parameterises it and the
query cache is not busted per timestamp.

**`AllVersions()` uses the same mechanism (added 2026-09-10).** Same marker + hook + root
replacement, differing only in how the history source is shaped: no period predicate, and
`historyRoot.Where(operation <> 3).OrderByDescending(valid_from).Select(h => new Policy { ... }).AsNoTracking()`.
The projection, the `AsNoTracking`, and every guard (first operator, non-temporal, `Include`,
`AsTracking`, `ExecuteUpdate`/`ExecuteDelete`, TPH, owned/complex) are shared with `AsOf` in
`HistoryQueryRootRewriter`.
- **Tombstone excluded (`operation <> 3`).** The delete tombstone (D5) carries the last column
  values before the delete but an empty interval `[ts, ts)`. Returned as a "version" it would be a
  data-duplicate of the final real version with `ValidFrom == ValidTo` — meaningless as a state
  snapshot and misleading in a timeline. So `AllVersions()` returns only inserts and updates; a
  deleted entity's timeline ends at the version that was open when it was deleted. The *when* / *who*
  of the delete is `History<T>()`'s job.
- **Default order `valid_from DESC`, baked into the rewrite.** `AllVersions()` returns
  `IQueryable<T>` (not `IOrderedQueryable<T>`), so a caller cannot append a bare `ThenBy`; a caller
  `OrderBy` / `OrderByDescending` replaces the default (standard LINQ — EF drops the overridden
  `ORDER BY`), a trailing `ThenBy` keeps newest-first primary.

**`History<T>()` uses the same mechanism, wrapping the entity in `Version<TEntity>` (added
2026-09-10).** `db.History<Policy>()` is an extension on `DbContext` (not `DbSet` — the doc shape
`db.History<Policy>()` won, and there is nothing a caller could put "before" a `DbContext`); it
builds `context.Set<TEntity>()` + a `MarkHistory` marker and lets the same hook rewrite it. The
history source has **no filter at all** and is ordered
`OrderByDescending(valid_from).ThenByDescending(history_id)`; the projection is a nested member-init
`h => new Version<TEntity> { Entity = new TEntity { … }, ValidFrom = (DateTimeOffset)…, ValidTo = …,
Operation = (VersionOperation)…, ChangedBy = …, … }`, then `AsNoTracking()`. Every guard (first
operator, non-temporal, `Include`, `AsTracking`, `ExecuteUpdate`/`ExecuteDelete`, TPH, owned/complex)
and `BuildBindings` for the inner entity are shared with `AsOf` / `AllVersions` in
`HistoryQueryRootRewriter`.
- **Tombstone included.** This is the one place it should be: `History<T>()` is the audit view, and
  the `operation = 3` row carries *when* and *who* of a delete (D5). It comes back as a `Version`
  with `Operation == VersionOperation.Delete`, `ValidFrom == ValidTo` (empty interval) and
  `IsCurrent == false`.
- **`history_id DESC` secondary sort.** The identity surrogate key is the only thing that always
  separates two rows written in one transaction (the delete's "close previous version" `UPDATE`
  keeps its row's `valid_from`, the tombstone `INSERT` gets a fresh `history_id`), so it makes the
  order a stable total order for interval assertions in tests.
- **`ValidFrom` / `ValidTo` are `DateTimeOffset`.** The history columns are `timestamptz` mapped as
  `DateTime` (UTC); the projection casts with `(DateTimeOffset)`, which EF translates as
  `p.valid_from::timestamptz`. The open version's `valid_to` is `'infinity'`, which Npgsql surfaces
  as `DateTimeOffset.MaxValue`; `Version<T>.IsCurrent` is `ValidTo == DateTimeOffset.MaxValue` — no
  magic `null` (D5). `Operation` is a new public `enum VersionOperation : short { Insert = 1,
  Update = 2, Delete = 3 }` (mirrors the internal `HistoryOperation`; kept separate so the writer
  enum stays internal).

**The spike question — does root replacement survive composition with `Where`/`OrderBy`/`Select`/
`First`? — is answered yes.** `db.Policies.AsOf(t).Where(p => p.Premium > 0).OrderBy(p => p.Number)
.Select(p => p.Status).First()` compiles to one statement —
`SELECT p.status FROM policies_history AS p WHERE p.valid_from <= @asOf AND p.valid_to > @asOf AND p.premium > 0 ORDER BY p.number LIMIT 1`
— with no client evaluation. Covered by `AsOfQueryTests` in `Hindsight.IntegrationTests`
(Testcontainers): point-in-time between versions; empty before the first version; half-open boundary
at `valid_from`; deleted-entity tombstone never matched; composite key; enum-as-string / `jsonb` /
`text[]` projected back into the entity; `Concat` of two `AsOf`; `AsOf` on both sides of a join;
`AsOf` inside a `Contains` subquery — all translate. The canonical SQL has a Verify snapshot.
`AllVersionsQueryTests` covers the same ground for `AllVersions()`: N versions returned newest
first; `Where` / `Select` / `Count` composition; the caller `OrderBy` replacing the default order;
the tombstone excluded after a delete; composite key; enum / `jsonb` / `text[]` round-trip;
`Concat` of `AllVersions()` and `AsOf()` in one tree; and every guard. Its canonical SQL
(`… WHERE operation <> 3 AND … ORDER BY valid_from DESC`) has a Verify snapshot.
`HistoryQueryTests` covers `History<T>()`: every row returned including the tombstone, newest first;
contiguous intervals with `IsCurrent` only on the open one; the empty-interval tombstone; `Where` on
an `Entity` property and a `Select` mixing metadata and `Entity` properties each producing **one**
SQL query (no client evaluation — the nested member-init projection composes); the change-context
columns via a registered `IChangeContextProvider`; composite key; enum / `jsonb` / `text[]` on the
`Entity` snapshot; no-tracking; and every guard. Its canonical SQL (all entity + period + context
columns, `ORDER BY valid_from DESC, history_id DESC`) has a Verify snapshot. The shared rewrite
lives in `HistoryQueryRootRewriter`; the marker scan and Include/AsTracking rejection in
`HindsightQueryExpressionInterceptor` recognise all three markers.

`FromSql` (the fallback named below) also composes, but stays the fallback: it forces every mapped
property into the `SELECT` list, so an `Exclude()`-d non-nullable column needs a sentinel value in
the SQL — meaningless data in a real column, which rule 2 discourages.

Two things root replacement does not give for free, both handled in the rewrite (for `AsOf`,
`AllVersions` and `History<T>` alike — for `History<T>` the mapped entity is nested inside the
`Version<TEntity>` the `Select` produces, and EF tracks it just the same):
- **No-tracking (D7).** A `Select` that materialises a mapped entity with all key properties set is
  tracked by EF, so the rewrite appends `AsNoTracking()`. `.AsTracking()` throws — the hook
  rejects an explicit tracking operator rather than let it override.
- **`Include` (D8).** EF would throw its own `InvalidOperationException` about `Include`
  after `Select`; the hook detects `Include` / `ThenInclude` next to a history marker first and
  throws `NotSupportedException` naming D8.

Further rules the hook enforces (all three markers): the marker must be the first operator on the
query (else `InvalidOperationException` — move it before `Where`/`OrderBy`/…; not reachable for
`History<T>()`, which starts from the `DbContext`); on a non-temporal entity it
throws `InvalidOperationException` naming the entity; on an inheritance hierarchy or an entity with
owned / complex members it throws `NotSupportedException` (those column sets are not reconstructable
from the history table — use `FromSql`) — both are now unreachable in practice, since `IsTemporal()`
itself already rejects either shape at model finalization (D9), but the guard stays as the same
defense-in-depth this file uses elsewhere.

**Blocking a re-attached snapshot from being saved (D7 sentence 2) — done, 2026-09-10.** The result
is detached and no-tracking, but nothing in EF stops `Update` / `Attach` / `Add` / `Remove` +
`SaveChanges` on it, which would write a stale snapshot back as the current version and generate
spurious history — the silent-wrong outcome rule 2 exists to prevent. No public materialization hook
fires for the rewriter's `new TEntity { … }` projection (spike: `IMaterializationInterceptor` is
skipped for a mapped-type `MemberInit`), and wrapping the projection body in a marker call is an
optimization barrier that stops `Where` / `OrderBy` / `Select` over the entity's members from
translating. So the mark is applied *outside* everything the caller composed:
`HindsightQueryExpressionInterceptor`, after the rewrite, appends a trailing client-evaluated
`Select(HistoryOrigin.Tag)` (or `TagVersion` for `History<T>`) when — and only when — the query's own
result sequence descends through `Where` / `OrderBy` / `Concat` / … to one of the rewriter's nodes
*and* still has that node's element type (a scalar / DTO projection, or a history query used only in
a subquery, is left alone). `HistoryOrigin` keeps the marks in a `ConditionalWeakTable`, so they add
no field to the user's type, do not root the instance, and survive `ChangeTracker.Clear()`.
`HistorySnapshotGuardInterceptor` (an `ISaveChangesInterceptor` registered in both writer modes)
runs before the history writer and throws `InvalidOperationException` if any `Added` / `Modified` /
`Deleted` entry is a marked instance. No new public surface — `Tag` / `TagVersion` are `internal`,
called from an expression EF compiles. The trailing `Select` makes the reconstructed entity no longer
the leaf projection, so the canonical-SQL Verify snapshots now show the history columns unaliased
(`SELECT p.id, p.status, …` instead of `p.id AS "Id", …`) — same table, columns and order, one query,
no client evaluation beyond the `Tag` call itself.

Revisit if: EF Core changes `IQueryExpressionInterceptor` semantics or removes the public
`EntityQueryRootExpression(IEntityType)` constructor (the `efcore-preview` canary covers this); or a
store type appears that the `EF.Property<T>` projection cannot round-trip.

## D13. Trigger DDL is emitted by a decorator over `IMigrationsSqlGenerator` — resolved by spike, 2026-09-11

The Trigger writer (D3) needs the migration to emit `CREATE FUNCTION` + `CREATE TRIGGER`, and to
re-emit `CREATE OR REPLACE FUNCTION` whenever the versioned column set changes. **Spike question: can
that be done through EF Core / Npgsql public API only (golden rule 1)?** Yes.

`HindsightMigrationsSqlGenerator` is a decorator registered (in Trigger mode) as `IMigrationsSqlGenerator`
over the provider's own generator. In `Generate(operations, model, options)` it walks the operation
list and injects plain `SqlOperation` entries — the trigger function + trigger after a history table's
`CreateTableOperation`, a `DROP FUNCTION … CASCADE` before its `DropTableOperation`, and one
`CREATE OR REPLACE FUNCTION` after any `AddColumn`/`DropColumn`/`AlterColumn`/`RenameColumn` on a
temporal main table or its history table — then hands the rewritten list to the inner generator.
Everything it needs (`Hindsight:IsHistoryTable`, `HistoryEntityType`, `IsTemporal`, `Orphaned`, the
mirrored column set, key columns, period column names) is read from the `IModel` passed to `Generate`;
those annotations reach it verbatim on both the design-time model and a migration's compiled
`TargetModel` (D6 established the same for the snapshot model).

**De-temporalizing an entity entirely** (`IsTemporal()` removed, or the entity type removed from the
model) is a different case from a dropped history table: D6's whole-entity-type orphaning clones the
history entity type unchanged from the previous snapshot precisely so the differ sees no difference and
never emits a `DropTableOperation` for it — which also means there is no operation to hang a
`DROP FUNCTION … CASCADE` on, and the trigger would otherwise keep firing on the main table forever,
silently writing into a history table the model no longer considers temporal at all. Fixing this at
`Generate` time would need the *previous* migration's model to compare against, which the generator does
not have. Instead the convention itself signals the transition, once: `CloneOrphanedHistoryEntityType`
tags the freshly orphaned history entity type with `Hindsight:OrphanedTriggerPending` only when the
previous snapshot's entity was not already `Orphaned` — i.e. only on the one migration where the
transition actually happens. That fact is then baked into that migration's compiled `TargetModel`
forever, the same mechanism that already makes `Orphaned` itself durable; a later `migrations add` sees
the snapshot already `Orphaned` and does not set the pending flag again. `Generate` reads
`OrphanedTriggerPending` independently of the trigger-model matching above (there is no live source left
to match it against) and appends one `DROP FUNCTION IF EXISTS … CASCADE` — `CASCADE` removes the
dependent trigger on the main table with it. Not a golden-rule-3 violation: dropping a trigger drops no
data, only a future write path.

**Why a decorator, not a subclass of `NpgsqlMigrationsSqlGenerator`:** that type's only public
constructor takes `Npgsql…Infrastructure.Internal.INpgsqlSingletonOptions` — an `.Internal` type,
which golden rule 1 forbids naming. The decorator resolves `NpgsqlMigrationsSqlGenerator` (a public
type) as a DI service so the container fills that parameter; Hindsight source never names the internal
type, and there is no `protected` override to break on a minor release. The extra DDL is `SqlOperation`
(core, stable), the identifiers pass through `ISqlGenerationHelper.DelimitIdentifier`.

Covered by `TriggerDdlTests` (generates the DDL through the real pipeline, applies it to PostgreSQL,
inspects `pg_proc` / `pg_trigger` / `information_schema`, drives insert/update/delete with raw SQL and
asserts half-open intervals + tombstone; Verify snapshot of the function + trigger),
`TriggerHistoryWriterTests` (every history scenario in both writer modes), and
`OrphanedHistoryTriggerTests` (unit: the pending flag is set on the transition migration only and not on
any later one; integration: migrates a temporal entity, removes `IsTemporal()`, migrates again for real,
and asserts a raw SQL insert against the main table no longer writes a history row).

Revisit if: Npgsql stops registering `NpgsqlMigrationsSqlGenerator` as a resolvable concrete service,
or changes `MigrationsSqlGenerator.Generate`'s contract (the `efcore-preview` canary covers this).

## D14. Period-range GiST index — implemented 2026-09-11

D5 has promised a GiST index on the period range since before any code existed; it was never actually
built (found while auditing D5 against the convention that builds history tables — no regression, a
doc that got ahead of the code). `AsOf(t)` and `History<T>`'s range-overlap predicate
(`valid_from <= t AND valid_to > t`) is served by the D5 `(pk columns, valid_from desc)` btree index
only when the query also filters on the leading key columns; a query that does not (or a bare
`AllVersions()`/`History<T>()` scan filtered on a non-key column) forces a sequential scan of the whole
history table. A real range index closes that gap.

**Shape:** one column, `gist (tstzrange(valid_from, valid_to))` — no primary-key columns included.
Range types have a native GiST opclass in PostgreSQL core, so this needs no extension; a composite
GiST index that also covered the key columns (an exclusion-constraint-style index) would need
`btree_gist` for the scalar key part, which would contradict README's "no database extensions, no
superuser" non-goal for a gain the version index — most `AsOf` calls do filter on the key — already
covers. A single-column range index also needs no change for a composite primary key, since it never
references key columns at all: there is exactly one index shape regardless of how the entity is keyed.

**Emission:** same mechanism as the Trigger DDL (D13) — `HindsightMigrationsSqlGenerator` reads
`Hindsight:IsHistoryTable` / `Hindsight:PeriodStart` / `Hindsight:PeriodEnd` off the `IModel` passed to
`Generate` and injects a `CREATE INDEX` `SqlOperation` right after the history table's
`CreateTableOperation`. Unlike the trigger function, the index is never re-emitted: a history table's
period columns and their types never change after creation, so there is nothing to keep in sync.
`HindsightMigrationsSqlGenerator` was previously registered as `IMigrationsSqlGenerator` only in
`HistoryWriter.Trigger` mode (nothing else needed it); the index has nothing to do with which writer is
configured, so the decorator is now registered in both modes, and takes the configured `HistoryWriter`
so it knows whether to also emit the trigger DDL.

**Surviving D6 orphaning:** whole-entity-type orphaning (D6) never drops or recreates a history table —
that is the entire point of `CloneOrphanedHistoryEntityType` — so an index created back when the entity
was still temporal is never touched by a later de-temporalizing migration; nothing had to be added to
the orphaning path itself. (This differs from the Trigger function, which does need an explicit
`DROP FUNCTION … CASCADE` on that transition — D13 — because the function lives outside the table and
the differ has no operation to hang it on. A physical index has no such problem: it is dropped only if
its table is, and D6 guarantees the table never is.)

Covered by `HistoryPeriodRangeIndexTests` (single-column-key and composite-key entities, both writer
modes — `pg_index`/`pg_am` confirm the `gist` access method and `pg_indexes` confirms the definition)
and `HistoryTableRetentionOnDetemporalizeTests` (the index survives the same de-temporalize migration
D6 already covers for the table itself).

## D15. History entity identity is independent of its table name — resolved 2026-09-12

A rename of a temporal entity's main table (`ToTable(...)`) or its history table
(`UseHistoryTable(...)`) must show up in the migration as a `RenameTableOperation`, so history is
renamed in place and keeps growing under the new name, instead of being dropped-and-recreated under a
fresh identity.

**The bug.** `HistoryEntityTypeConvention.BuildHistoryEntityType` used the history table's own
(possibly derived) name as *both* the model identity of its `SharedTypeEntity` and its mapped table
name (`modelBuilder.SharedTypeEntity(historyTableName, ...)`, then `.ToTable(historyTableName, ...)`).
The differ pairs shared-type entities by identity, not by mapped table name, so any change to that
name also changed the identity — the differ could never see "the same entity, renamed", only an
unrelated new entity appearing. D6's own retention mechanism then kept the *old* identity alive too
(correctly recognizing it as "no longer rebuilt this pass", the same signal a de-temporalized entity
gives it), so nothing was ever destroyed — but old and new history ended up split across two
disconnected tables, invisible to each other for `AsOf` / `AllVersions` / `History<T>`. Verified against
real PostgreSQL before any fix: renaming a temporal entity's `ToTable(...)` produced exactly this split,
with no crash and no data loss, just silently divergent history. (The originally suspected crash —
`INSERT INTO <stale name>` failing because a `RenameTableOperation` slipped past
`HindsightMigrationsSqlGenerator.Rewrite` unhandled — never occurs: a history table's identity coupling
to its own name meant the differ never emitted a `RenameTableOperation` for it in the first place, only
for the main table, and PostgreSQL's `ALTER TABLE ... RENAME` already carries a trigger to the renamed
table on its own, since a trigger is tracked by the relation's OID, not its name.)

**Fix.** The identity is now `<source>#History` — styled after EF's own generated names for entities
that don't come from a CLR type directly (e.g. `Blog.Owner#Owner`; `#` cannot appear in a CLR type
name, so this can't collide with a real entity) — stable across any later `ToTable` / `UseHistoryTable`
call. `ToTable(...)` on the history builder still points that stable identity at whatever the current
table-name computation produces, so a rename of either table is now a genuine, single-entity
`RenameTableOperation`.

**Backward compatibility (grandfathering).** Every entity already temporal before this fix has its
history identity equal to its own table name, as recorded in the last migration's `ModelSnapshot`.
Switching everyone unconditionally to the new `#History` scheme would make the differ see their live
history table disappear (wrong identity) and an unrelated one with the new identity appear —
`DropTable` + `CreateTable` for existing production history, on the very next migration after
upgrading Hindsight, with no rename involved at all (golden rule 3). Instead,
`HistoryEntityTypeConvention` reads the previous `IMigrationsAssembly.ModelSnapshot` (same mechanism D6
already uses for orphaning) and, when the source entity already has a recorded
`Hindsight:HistoryEntityType` identity there, reuses that identity forever — the same "propagate
forward" trick D6 uses for `Orphaned`. Only a source becoming temporal for the first time after this
fix gets the new `#History` scheme. A deployment that never renames anything sees no migration diff
from upgrading Hindsight alone; one that does rename later still gets a true `RenameTableOperation`,
because its grandfathered identity is stable too. (The snapshot is now resolved once per model-build
pass, shared by history-entity construction, orphaned-column restoration and orphaned-table
restoration, instead of once per call site as before — same guarded-by-`_resolvingSnapshotModel`
re-entrancy behavior, just computed once.)

**`HindsightMigrationsSqlGenerator.Rewrite` handles `RenameTableOperation`**, matched by whether the
operation's *new* (schema, name) is a live trigger model's history table or main table:

- **History table renamed:** the function's own name and its `INSERT INTO` target both embed the old
  table name literally, baked in at the last `CreateFunction` — genuinely stale after the rename.
  Recreating it is deferred until *after* every operation in the migration has been emitted, not done
  in place right after the `RenameTableOperation`: when the same migration also renames the main table
  (the default-suffix-naming case, since both names derive from the same source and rename together),
  that rename's own operation can appear *later* in the list, and emitting `CREATE TRIGGER ... ON
  <main table>` before that later rename has run would name a table that does not exist yet under that
  name. Deferring to the end (the same place `needsRefresh` already replays column-driven
  `CREATE OR REPLACE FUNCTION`s) guarantees every rename in the migration has already executed. Found
  by writing the naive in-place version first and watching it fail against real PostgreSQL with
  `relation "policies2" does not exist` — exactly the failure the fix now avoids.
- **Main table renamed, history table's own name unchanged** (an explicit, unchanged
  `UseHistoryTable(...)`): nothing is emitted. Verified against real PostgreSQL: a plain
  `ALTER TABLE ... RENAME` carries the trigger to the renamed table automatically (a trigger is tracked
  by the relation's OID, not its name), and the function body never names the main table at all (only
  `NEW` / `OLD`), so a write through the renamed table succeeds with zero additional DDL.

Covered by `TableRenameTriggerDdlTests` (Testcontainers: a main-table rename with default history
naming — asserts both `RenameTableOperation`s, that no `CreateTable`/`DropTable` appears, and that
history continues under the new name with no gap across the rename; a history-only rename via
`UseHistoryTable`; a main-table rename with a fixed history name, asserting no function/trigger DDL is
emitted at all) and the existing `HistoryEntityTypeConventionTests` / `OrphanedHistoryColumnTests` /
`OrphanedHistoryTriggerTests` (updated to resolve a temporal entity's history entity type through its
`Hindsight:HistoryEntityType` annotation — the same idiom production code already used — rather than
assuming the identity equals the table name, which was only ever true by construction, not by
contract).

Revisit if: EF Core's differ starts pairing shared-type entities by something other than their `Name`;
or the grandfathering lookup needs to survive the *source* entity itself being renamed (a different CLR
type mapped onto the same table) — untested, and likely already broken the same way any EF entity
identity change is.

## D16. Change-context trust model, and a proposed `session_user` audit column — open question

**Documented, 2026-09-12** (see `docs/articles/configuration.md` → Trust model): the change-context
columns (`changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`) are not tamper-resistant
against anything with an ordinary database connection. Under `HistoryWriter.Trigger` in particular,
they round-trip through `set_config('hindsight.*', ..., true)` / `current_setting(...)` — a
transaction-local session setting with no authentication behind it. Any session with the same
(non-superuser) database privileges as the application can run
`SELECT set_config('hindsight.changed_by', 'someone-else', true)` immediately before its own `UPDATE`
and produce a history row indistinguishable from one the application wrote. This was always implied by
D3 (the application is trusted; the trigger cannot itself authenticate anything) but was never spelled
out for a reader coming from the "audit trail" / "who did it" framing D5 uses. Fixed as a docs-only
change; no code changed.

**Open question this entry exists to record: should Hindsight also capture something PostgreSQL itself
guarantees, as a second, defense-in-depth column?**

`session_user` (no-arg, stable for the life of a session, guaranteed by PostgreSQL's own connection
authentication — immune to `set_config`) is the natural candidate. Proposed shape, if this goes ahead:
an **additional**, always-populated fixed history column —
`db_session_user text not null default session_user` — alongside (not replacing) the existing
application-supplied columns. It answers a different question than `changed_by`: "which database
role/connection actually executed this write", not "which end user". The two are usually different
values in practice — a pooled application connection's `session_user` is typically one shared service
role, so `db_session_user` mostly tells you "yes, this came in through the app's own role" rather than
naming a human; its value is in the negative case, catching a write that did *not* come through that
role at all. Not a substitute for the docs fix above: it narrows what an attacker with the
application's own stolen credentials can fake (nothing — `session_user` is exactly the role they
authenticated as), but does nothing against someone who genuinely holds the application's credentials
and abuses them from `psql` instead of through the app, since `session_user` is unchanged either way.

**Investigation (spike, not shipped — reverted after confirming the mechanism):** is
`DEFAULT session_user` really "free" — no interceptor or trigger-function code change at all?

Both writers already construct their `INSERT`'s column list *explicitly*, naming only the columns they
know about, rather than relying on `INSERT INTO table VALUES (...)` positional-all-columns form:
- `HistoryRowWriter.TemplateCache.Build` (Interceptor) builds `columns`/`placeholders` from
  `row.VersionedColumns`, then the five context columns (`Writers/HistoryRowWriter.cs`) — nothing else.
- `HistoryTriggerSqlGenerator.CreateFunction`'s `insertColumns` is `model.EntityColumns` plus the fixed
  period/operation/context columns (`Migrations/HistoryTriggerSqlGenerator.cs`) — nothing else.

Neither list is generated from "every column on the history table minus some exclusion set" — it is
built up from what each writer explicitly knows to write. `history_id` is already proof this works:
it's a real `not null` column on every history table (`bigint generated always as identity`, added by
`HistoryEntityTypeConvention.AddSurrogateKey`) that appears in **neither** column list above, and
PostgreSQL fills it in on every physical `INSERT` regardless of writer mode, today, with zero
interceptor/trigger code aware of it. A plain `DEFAULT session_user` column follows the exact same
path: as long as `AddContextColumns` (or a new sibling method) adds it to the history entity type via
`HasDefaultValueSql("session_user")` without adding it to `HistoryRowWriter`'s or
`HistoryTriggerSqlGenerator`'s explicit column-building code, PostgreSQL populates it on every `INSERT`
those statements issue, in both writer modes, including bulk `ExecuteUpdate`/`ExecuteDelete` and raw
SQL under `HistoryWriter.Trigger` (D4) — those go straight through the trigger's own `INSERT`, which is
built the same way. Confirmed empirically against real PostgreSQL: added the column with an `ALTER
TABLE ... ADD COLUMN db_session_user text NOT NULL DEFAULT session_user` after the normal migration,
then exercised an insert/update/delete through `HistoryWriter.Interceptor` and through
`HistoryWriter.Trigger` (including a raw-SQL `UPDATE` bypassing `SaveChanges` entirely) — every
resulting history row had `db_session_user` populated with the connection's actual `session_user`,
with no change to `HistoryRowWriter` or `HistoryTriggerSqlGenerator`. The throwaway spike test that
proved this was removed after confirming the finding; it is not part of the tree.

**No wrinkle found on the "is it free" question** — Npgsql's batched/parameterised `INSERT` (Interceptor
mode) does not enumerate "all columns"; it enumerates exactly the placeholders `HistoryRowWriter` built,
so a column absent from that list is, from Npgsql's point of view, simply not mentioned, and PostgreSQL
applies its `DEFAULT` exactly as it would for any manual `INSERT` that omits a column. Same reasoning
for the trigger's plpgsql `INSERT`.

**What is *not* free, and why this is not implemented in this PR:** the column addition is a real,
permanent public-surface and schema change, not a code-path trick:
- a new entry in `HindsightHistoryColumns.cs` and a new fixed column documented in D5's table;
- `Version<TEntity>` (the public read-side type, `Version.cs`) needs a new property (e.g.
  `DbSessionUser`) to expose it to `History<T>()` callers, which is a new public API surface: XML doc,
  `PublicAPI.Unshipped.txt` entry, an update to every article that lists `Version<T>`'s members;
  `HistoryQueryRootRewriter`'s projection needs to bind it, same as the other context columns;
- every DDL Verify snapshot (`TriggerDdlTests` and friends) and every test that asserts a history
  table's exact column set needs updating to include the new column — not optional, since those tests
  exist specifically to catch an undocumented column drift;
- a migration-generation concern the spike did not need to solve: existing history tables predating
  this column need the same `ADD COLUMN ... DEFAULT session_user` migration path already used for any
  other newly-added fixed column, plus deciding whether backfilling existing rows is in scope (out of
  scope for v1, consistent with D6 treating pre-existing gaps as immutable history) or left `NULL` for
  rows written before the migration — the column would need to be nullable on the actual history table
  even though new rows always populate it, unless a one-time backfill runs as part of the same
  migration.

Per CLAUDE.md rule 9, this stays an open question pending a maintainer decision on:
1. whether the audit-trail value (catching a write that bypassed the application's role entirely) is
   worth a new permanent column on every history table, given it does not defend against the more
   likely threat (a legitimate holder of the application's credentials using them outside the app); and
2. the exact public shape — column name (`db_session_user` vs. something else), whether `current_user`
   is also worth capturing alongside `session_user` (they differ under `SET ROLE` / `SECURITY DEFINER`
   functions — `session_user` is who actually authenticated, `current_user` is whose privileges the
   statement is currently running with; `session_user` is the one that matters for this threat model
   since it cannot be changed within a session without re-authenticating), and whether it belongs in the
   base column set or as an opt-in (`IsTemporal(t => t.WithDbSessionUser())`) given it is not
   universally wanted and (unlike everything else in D5) is PostgreSQL-authentication-shaped rather than
   application-shaped.

## Open questions (resolve in the spike, then move up)

- **D16**: add a `db_session_user` (or similarly named) column, populated by PostgreSQL's own
  `DEFAULT session_user`, as a defense-in-depth audit column alongside the existing application-supplied
  change-context columns? The mechanism is confirmed free (no interceptor/trigger code change); the
  decision pending is whether the feature is wanted at all, its exact column name, and whether
  `current_user` should also be captured — see D16 for the full write-up.

### Should `HistoryWriter.Trigger` become the default in v2.0? — opened 2026-09-12

Not decided. `HistoryWriter.Interceptor` is `= 0` and therefore what `UseHindsight()` gives a caller
who never calls `UseHistoryWriter(...)` — see `Infrastructure/HindsightOptionsExtension.cs`. D3
already recommends `Trigger` in production and README/`docs/articles/history-writers.md` say so too,
but the silent default is still the structurally weaker writer.

**The case for switching.** `Trigger` has no known correctness gap versus `Interceptor`: it is immune
to the clock-skew and backwards-clock exposure D3 documents for `Interceptor` (timestamps come from
`now()` inside the writing transaction, not an application `TimeProvider` that can disagree across
instances), and it sees `ExecuteUpdate` / `ExecuteDelete` / raw SQL / other-process writes (D4), which
`Interceptor` structurally cannot. A new user who never reads the writer comparison table currently
gets the weaker guarantees by default; that is exactly the kind of trap rule 2 exists to avoid at the
API level, not just in prose.

**The case for keeping `Interceptor` as the default.** D3 already names `Interceptor`'s one legitimate
remaining use case: "environments where trigger creation is forbidden by policy" — some managed
PostgreSQL setups and some organizations' database-permission policies do not grant an application
role `CREATE FUNCTION` / `CREATE TRIGGER`, only `SELECT`/`INSERT`/`UPDATE`/`DELETE` on specific tables.
For those callers, `Trigger` is not just non-default, it is unusable, and a default flip would turn
`UseHindsight()` alone into a runtime failure on `dotnet ef migrations add` (a `CREATE FUNCTION` the
role can't execute) for anyone in that position who upgrades without reading the changelog.

**The migration path this needs, if we do it.** Flipping the default is a breaking behavior change
(D11: MinVer semver from git tags), and it is not just a code default — an existing 1.x user who
upgrades to 2.0 with no explicit `UseHistoryWriter(...)` call would silently: (a) start getting
`now()` timestamps instead of `TimeProvider` ones (breaks any test or code that reasoned about
injected time), and (b) get trigger DDL — `CREATE FUNCTION` / `CREATE TRIGGER` — injected into their
*next* `dotnet ef migrations add`, which they did not ask for and which may fail outright if their
migration role lacks the privilege. A bare "default changed" line in `CHANGELOG.md` is not enough.
The 2.0 upgrade guide needs its own migration-path section spelling out, roughly: "2.0 changes the
default `HistoryWriter` from `Interceptor` to `Trigger`. If you never called `UseHistoryWriter(...)`
explicitly, upgrading will change your history's timestamp source from `TimeProvider` to `now()` and
add trigger DDL to your next migration. If you need to defer this — including if your database role
cannot create functions or triggers — pin `UseHistoryWriter(HistoryWriter.Interceptor)` explicitly
before upgrading, which keeps 1.x behavior unchanged."

**What would make us revisit/resolve this:** a decision, before a 2.0 milestone is cut, on whether the
default flips (see D11 — a 2.0 milestone should track this so it isn't decided by drive-by PR); if it
does, the upgrade guide above ships in the same release, not after.
