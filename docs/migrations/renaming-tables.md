---
order: 30
---

# Renaming tables

Rename the main table with `ToTable(...)`, or the history table with `UseHistoryTable(...)` inside
`IsTemporal(...)`, and the migration renames it in place with a `RenameTableOperation`. History keeps
growing under the new name: nothing is split, and everything `AsOf`, `AllVersions` and `History<T>`
could read before the rename they can still read after it.

| you rename | the migration renames | trigger writer |
|---|---|---|
| the main table, history table at its default name | both tables, since the default history name follows the main table | drops the function and trigger under the old name and recreates them under the new one |
| the main table, history table named with `UseHistoryTable(...)` | the main table only | nothing to do: PostgreSQL carries the trigger along with the table |
| the history table only | the history table only | drops and recreates the function and trigger, whose names and body contain the history table's name |

In both writer modes, the history table's period-range index is renamed along with the table.
PostgreSQL's `ALTER TABLE … RENAME` does not rename indexes, and a stale name would later collide with
the index of another entity whose history table happens to take the freed-up name. (The version index
is an ordinary EF Core index, which EF Core renames itself.)

This works because a history table's identity in the model does not depend on its name. Upgrading
Hindsight without renaming anything produces no migration changes.

## Moving a table to a different schema

Change the schema in `ToTable("name", "schema")` or `UseHistoryTable("name", "schema")`, and the
migration moves it with `ALTER TABLE ... SET SCHEMA`. History keeps growing under the new schema, the
same way a name-only rename keeps it growing under the new name.

PostgreSQL moves every object a table owns — its indexes included — into the new schema automatically
when the table moves, so the period-range index needs no DDL of its own for this. The trigger function
is a standalone object, unaffected by the table's own move, and is recreated under the new schema the
same way it is for a name change.
