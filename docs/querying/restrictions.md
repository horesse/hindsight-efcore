---
order: 40
---

# Rules for historical queries

These apply to `AsOf`, `AllVersions` and `History<T>`. Each rule exists because breaking it would
return a result that looks right and is not; in every case Hindsight throws instead.

## History is read-only

Results are **always no-tracking**: detached snapshots that leave the change tracker untouched, and
adding `.AsTracking()` throws.

Saving a snapshot back also throws. Re-attaching one with `Update`, `Attach`, `Add`, `Remove` or by
setting its state, and then calling `SaveChanges`, would write the past over the present and create a
spurious version; it throws `InvalidOperationException` instead. This covers `Version<T>.Entity` too.

To bring back an old value, copy it onto the current, tracked entity and save that:

<<< @/snippets/Querying.cs#roll-back-a-value

## No `Include`

`Include` and `ThenInclude` after a historical query throw `NotSupportedException`. Reading related
entities *as of* a point in time is a join on two periods, and a plain join would pair versions that
never existed together.

Load related rows with a second historical query, keyed on the foreign key you already have.

## `AsOf` and `AllVersions` come first

Put them directly on the `DbSet`, before `Where`, `OrderBy` or `Select`.
`db.Policies.Where(…).AsOf(t)` throws. (`History<T>()` starts from the `DbContext`, so there is
nothing to put before it.)

## No `ExecuteUpdate` or `ExecuteDelete`

`ExecuteUpdate` and `ExecuteDelete` on a historical query throw `NotSupportedException`. Left alone,
EF Core would turn the query into an `UPDATE` of the **history table** itself and silently rewrite the
audit trail. Select the current rows with a normal query and run `ExecuteUpdate` or `ExecuteDelete` on
that.

## Temporal entities only

A historical query on an entity that is not temporal throws `InvalidOperationException` naming the
entity. So does one on an entity that used to be temporal: its
[history table is kept](/migrations/removing-temporal) for the data, but it is no longer queryable with
LINQ.

## Standalone entities only

Entities in an inheritance hierarchy, or with owned or complex members, cannot be temporal (see
[Model validation](/configuration/model-validation)); a historical query on one throws
`NotSupportedException`. Read the history table with `FromSql` if you have to.
