---
order: 40
sidebarTitle: Removing IsTemporal()
---

# Removing `IsTemporal()`

Remove the `IsTemporal()` call from an entity, or remove the entity from the model altogether, and
the migration **keeps the history table**, columns, data and indexes included. The same rule as for a
[removed property](/migrations/schema-evolution#removing-a-property) applies one level up: the table
stays in the model, tagged `Hindsight:Orphaned`, so no generated migration ever drops it.

- If the entity is still mapped, its main table keeps evolving normally. Only the history table is
  pinned as it was.
- The history table is kept for its data, not for queries. `AsOf`, `AllVersions` and `History<T>`
  require a temporal entity, so they throw `InvalidOperationException` for this one. Query the table
  with SQL if you need to.
- Once orphaned, it stays in every later model, so nothing has to rediscover it.

## With the trigger writer

This is the one case where a generated migration drops something on its own. The trigger lives on the
*main* table and would otherwise keep writing into a history table the model no longer considers
temporal. So the migration that removes `IsTemporal()` runs `DROP FUNCTION … CASCADE`, which also drops
the trigger. No data is deleted; only the writing stops. This happens once, in that migration.

## Dropping the history table

If you really want the history table gone, write a migration that drops it yourself. Hindsight never
will.
