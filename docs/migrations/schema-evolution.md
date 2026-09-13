---
order: 20
---

# Evolving a temporal entity

History tables are ordinary entity types in the EF Core model, so `dotnet ef migrations add` sees
them and generates their DDL along with the main table's. One rule decides *what* it generates:
**a generated migration never destroys history.**

| you change the entity | main table | history table |
|---|---|---|
| add a property | column added | column added; existing versions get `NULL` |
| remove a property | column dropped | column **kept**, nullable |
| rename a property | column renamed | new column added; the old one is **kept**, nullable |
| change a property's type | column altered | column altered |
| remove a primary-key property | — | **rejected** |

## Adding a property

Add the property to the entity. The migration adds the column to both tables. Existing history rows
get `NULL`, or the column's default if it has one.

## Removing a property

Remove the property from the entity. The migration drops the column from the main table and **keeps it
in the history table**, made nullable. Old versions still hold data in it, and dropping the column
would destroy the history this library exists to keep.

Hindsight does this by keeping the column in the model: on every model build it reads the previous
model snapshot and re-adds each history column that has lost its property, tagged
`Hindsight:Orphaned`. The migrations differ therefore never sees a column to drop. A model build with
no other changes scaffolds an empty migration.

If you really want a history column gone, write the migration that drops it yourself. Hindsight never
will.

## Renaming a property

EF Core's rename detection applies to the main table. In the history table a rename becomes *remove
plus add*: the new column appears, and the old one stays as a nullable orphan with its data.

This is deliberate. In history, a rename cannot be told apart from a change of meaning, and guessing
wrong would silently rewrite old versions.

## Changing a property's type

Both tables get the `ALTER COLUMN`. If the conversion can fail on existing data, as when narrowing a
type, the migration fails on the history table first. Fix the data, or widen the type instead.

## Removing a primary-key property

Rejected with `InvalidOperationException` when the model is built. The history table's version index
and the writer's "close the previous version" step are keyed on the primary key. Keep the property, or
[stop versioning the entity](/migrations/removing-temporal).

## With the trigger writer

Any migration that adds, removes or renames a column of a temporal entity also replaces the trigger
function (`CREATE OR REPLACE FUNCTION`), so it always writes the current set of columns:

- **added column**: the new function copies it into every new version;
- **removed or renamed column**: the old column stays in the history table and the new function stops
  writing it. Old versions keep their values; new versions get `NULL` there.

Only the function body changes; the trigger definition and the history table are never dropped.
