# Schema evolution

History tables are ordinary entity types in the EF Core model, so `dotnet ef migrations add` sees
them and generates the DDL. The rules below decide *what* it generates.

## Adding a column

Add the property to the entity. The migration adds the column to both the main table and the history
table. Existing history rows get `NULL` (or the column default) in the new column.

## Removing a column

Remove the property from the entity. The migration removes the column from the main table and
**keeps it in the history table**, altered to nullable. Old versions still hold data in it; dropping
the column would destroy history that the whole library exists to preserve.

If you really want it gone, write a migration that drops it explicitly. Hindsight will never do that
for you.

## Renaming a column

EF Core's rename detection applies to the main table. In the history table a rename is treated as
"remove + add": a new column appears, the old one stays nullable. This is deliberate — a rename in
history is not distinguishable from a semantic change, and guessing wrong silently corrupts old versions.

## Changing a column type

Both tables get the `ALTER COLUMN`. If the conversion can fail on old data (narrowing a type), the
migration fails on the history table first; fix the data or widen the type.

## Making an existing table temporal

Planned for v1.1: the migration will create the history table and seed it with the current rows as
the initial version (`valid_from = now()`, `operation = insert`). Until then, do it with a hand-written
`INSERT ... SELECT` in the migration.
