# Schema evolution

History tables are ordinary entity types in the EF Core model, so `dotnet ef migrations add` sees
them and generates the DDL. The rules below decide *what* it generates.

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

## Making an existing table temporal

Planned for v1.1: the migration will create the history table and seed it with the current rows as
the initial version (`valid_from = now()`, `operation = insert`). Until then, do it with a hand-written
`INSERT ... SELECT` in the migration.
