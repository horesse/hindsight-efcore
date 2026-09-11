# Schema evolution

History tables are ordinary entity types in the EF Core model, so `dotnet ef migrations add` sees
them and generates the DDL. The rules below decide *what* it generates.

## Indexes on the history table

The migration that first creates a history table also creates two indexes on it, regardless of
which [history writer](history-writers.md) you use:

- `ix_<history_table>_version` — a btree index on `(<primary key columns>, valid_from desc)`, an
  ordinary EF Core model index. It serves `AsOf` and `History<T>` calls that also filter on the
  entity's key.
- `ix_<history_table>_period` — `gist (tstzrange(valid_from, valid_to))`. `AsOf`'s and `History<T>`'s
  period-overlap predicate (`valid_from <= t AND valid_to > t`) is a range-containment test; without
  this index, a query that cannot also use the leading columns of the version index above forces a
  sequential scan of the whole history table. It is a single column — it does not reference the
  primary key at all, so it applies unchanged whether the entity has a single-column or a composite
  key — and needs no PostgreSQL extension: range types have a native GiST operator class in core.

Both indexes are created once, alongside the table, and never re-emitted or altered by a later
migration: neither the key columns nor the period columns' types can change once a table has been
created. Removing `IsTemporal()` from an entity (below) does not drop them either, because it does not
drop the history table.

## Adding a column

Add the property to the entity. The migration adds the column to both the main table and the history
table. Existing history rows get `NULL` (or the column default) in the new column.

## Removing a column

Remove the property from the entity. The migration removes the column from the main table and
**keeps it in the history table**, forced nullable. Old versions still hold data in it; dropping
the column would destroy history that the whole library exists to preserve.

How it works: on every model build the history convention reads the previous `ModelSnapshot` and, for
each history column that no longer has a live property behind it, re-adds it to the model as a
nullable shadow column tagged `Hindsight:Orphaned`. Because the column never leaves the model, the
migrations differ has nothing to drop — a generated migration never contains a `DropColumn`,
`DropTable` or narrowing `AlterColumn` on a history table. A build with no other change scaffolds an
empty migration.

One case is rejected: removing a property that is part of the entity's **primary key** throws
`InvalidOperationException`. The history version index and the writer's close-previous-version step
are keyed on those columns; restore the property, or drop `IsTemporal()` from the entity.

If you really want a history column gone, write a migration that drops it explicitly. Hindsight will
never do that for you.

## Removing `IsTemporal()` from an entity

The same rule applies one level up. Remove the `IsTemporal()` call — or remove the entity type from
the model entirely — and the migration drops nothing from the history table's schema: the whole table
stays, cloned as-is from the previous model snapshot and tagged `Hindsight:Orphaned`, the same way an
individual removed column is tagged today. A generated migration never contains a `DropTable` for a
history table just because its source stopped being temporal.

The entity's *main* table is unaffected by this — if the entity is still mapped, just no longer
temporal, its main table keeps evolving normally; only the history table is pinned. Once orphaned, a
history table stays in the model on every later build, same as an orphaned column, so it never needs
re-discovering. Querying it is not part of that: `AsOf()`, `AllVersions()` and `History<T>()` already
require the entity to be temporal and throw `InvalidOperationException` otherwise, so an orphaned
table is simply not reachable from LINQ any more — it is retained for the data, not for querying.

If you really want the history table gone too, write a migration that drops it explicitly, same as
for a column.

With the trigger writer, this is also the one case where the migration drops something on its own —
see [the trigger section](#the-trigger-when-you-use-historywritertrigger) below.

## Renaming a column

EF Core's rename detection applies to the main table. In the history table a rename is treated as
"remove + add": the new column appears, and the old one stays as a nullable orphaned column (same
mechanism as removing a column). This is deliberate — a rename in history is not distinguishable from
a semantic change, and guessing wrong silently corrupts old versions.

## Changing a column type

Both tables get the `ALTER COLUMN`. If the conversion can fail on old data (narrowing a type), the
migration fails on the history table first; fix the data or widen the type.

## The trigger, when you use `HistoryWriter.Trigger`

With the [trigger writer](history-writers.md), the migration that first creates a history table also
emits its `…_history_write()` function and `…_history_trg` trigger. After that, any migration that
adds, drops or renames a column on the temporal entity — or on its history table — carries a fresh
`CREATE OR REPLACE FUNCTION` so the function body always matches the current versioned column set:

- **added column** → the refreshed function copies it into every new history row.
- **removed / renamed column** → it stays on the history table as a nullable orphan (above), and the
  refreshed function simply stops writing it. Old rows keep their values; new rows get `NULL` there.

The trigger definition itself never changes, only the function body, so this is always a
`CREATE OR REPLACE` — never a `DROP` on the history table (golden rule 3). Dropping the whole history
table (only if you write that migration by hand) also drops the function, `CASCADE`.

Removing `IsTemporal()` from an entity is the one case where the migration **does** drop the function
(and, `CASCADE`, the trigger with it) even though the history table itself stays, per the section above.
The trigger lives on the *main* table, not the history table, so keeping the history table unchanged
would otherwise leave the trigger firing forever, writing new rows into a table the model no longer
considers temporal at all. This drop happens exactly once — on the migration where `IsTemporal()` is
removed — never again on any later migration for the same table.



## Making an existing table temporal

Planned for v1.1: the migration will create the history table and seed it with the current rows as
the initial version (`valid_from = now()`, `operation = insert`). Until then, do it with a hand-written
`INSERT ... SELECT` in the migration.
