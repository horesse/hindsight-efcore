---
order: 60
sidebarTitle: Retention and partitioning
---

# Retention and partitioning

History tables only grow. Hindsight never deletes history on its own: no migration and no writer
removes a row. When a table gets too big, or you may only keep history for a fixed period, you have
two tools:

- **Retention**: `PruneHistoryAsync` deletes history that ended before a cutoff, and records that
  cutoff, the *retention horizon*, so queries before it fail instead of returning wrong answers.
- **Partitioning**: split a large history table into monthly partitions. Old history can then be
  removed by dropping a whole partition, and queries skip partitions they don't need.

## Turning retention on

Retention is opt-in per entity:

<<< @/snippets/Retention.cs#with-retention

The next `dotnet ef migrations add` creates one small table, `hindsight_retention_horizon` (one row per
pruned entity), and a function, `hindsight_history_retained`, that queries use to check the horizon.
Nothing else changes, and nothing is deleted:

<<< @/../tests/Hindsight.IntegrationTests/HistoryRetentionTests.Retention_ddl_has_the_expected_shape.verified.sql

The function is written in PL/pgSQL, under either writer. A role that can create the table can also
create the function: PostgreSQL grants the use of PL/pgSQL to everyone by default. If your database
administrator revoked it, the migration fails with `permission denied for language plpgsql` (SQLSTATE
`42501`). The migration runs in one transaction, so the table isn't left behind and no data is
touched. Ask for `GRANT USAGE ON LANGUAGE plpgsql TO <migration role>`, or don't enable retention.

::: warning Removing `WithRetention()` later is rejected
Once a migration includes `WithRetention()`, removing it throws at model build. History may already be
pruned, and without the horizon check a query before it would quietly answer from what is left.
:::

## Pruning

Call `PruneHistoryAsync` from a scheduled job:

<<< @/snippets/Retention.cs#prune-history

It deletes every history row whose period **ended** at or before the cutoff: closed versions
(`valid_to <= cutoff`) and delete markers. The current version of a live entity is never deleted, however
old it is. An entity that was deleted before the cutoff disappears from history completely.

- **The horizon is recorded first**, in its own transaction, then rows are deleted in batches
  (`batchSize`, 10,000 by default), each batch in its own transaction. A large table is never locked by
  one long transaction, and a job that fails part-way loses nothing: the next run deletes the rest.
- **Inside your own transaction**, everything runs in that transaction instead, and rolls back with it.
- **The horizon never moves back.** Pruning again with an earlier cutoff deletes nothing new and keeps
  the later horizon.
- **The cutoff can't be in the future.** A cutoff later than the database's `now()` throws
  `ArgumentOutOfRangeException`: that history is still being written.
- The delete uses the history table's period index, so it doesn't scan the whole table.

## Queries after pruning

| query | instant at or after the horizon | instant before the horizon |
|---|---|---|
| `AsOf(t)` | exactly the same answer as before pruning | throws |
| `FromTo(from, to)`, `ContainedIn(from, to)` | same as before, when `from` is at or after the horizon | throws when `from` is before it |
| `AllVersions()`, `History<T>()` | return the history that is left | — |

"Throws" means PostgreSQL rejects the query with SQLSTATE `HS001`. EF Core surfaces it as a
`PostgresException`. The check runs once per query, before any row is read, so the query throws even
when no row would have matched:

<<< @/snippets/Retention.cs#horizon-error

`AllVersions()` and `History<T>()` have no instant to check, so they return what is left. An entity's
history may then start with an update instead of an insert. To tell whether a timeline is complete, or to
pick an instant `AsOf` can still answer, read the horizon:

<<< @/snippets/Retention.cs#history-horizon

It returns `null` for history that was never pruned.

## Partitioning

A partitioned history table is split into one partition per month, by **when each version ended**
(`valid_to`), plus one partition for the current versions:

- A version's partition only depends on when it was closed. Dropping a whole month's partition
  removes exactly the history that ended in that month, and never a current version.
- `AsOf(t)` skips every partition that ended before `t`, so recent queries stay fast however much old
  history there is.
- When a version is closed, PostgreSQL moves its row from the current partition to the month's partition.
  This costs one extra row write per update.

::: tip Why not partition by `valid_from`
A version's start never changes, but an entity that was created long ago and never updated has an
*old* `valid_from` and is still current. Dropping old `valid_from` partitions would delete current
versions.
:::

Hindsight doesn't generate partitioned tables: EF Core migrations can't express them, and converting a
table that already has data is a data migration. Instead, run this SQL once, from a migration you write
by hand (`migrationBuilder.Sql(...)`) or from `psql`. It is shown for the sample `Policy` entity; replace
`policies_history` and the key column `id` with your own:

<<< @/snippets/PartitionHistory.sql

It keeps every row, including `history_id`, and keeps the old table under a new name until you drop it.
Both writers, the trigger, the two indexes, `AsOf` and `PruneHistoryAsync` keep working unchanged; tested on
PostgreSQL 14 and 17. A column added by a later migration is added to every partition by PostgreSQL.

### Creating the next months

Partitions for future months must exist before versions are closed in them. Otherwise the rows land in
the `DEFAULT` partition, which every query scans, and a partition for that month can't be created while
the default one holds rows for it. Create them ahead of time: from a monthly job running the same
`CREATE TABLE … PARTITION OF` as the script, or with the
[`pg_partman`](https://github.com/pgpartman/pg_partman) extension.

### Dropping old partitions

Detach the partition and record the horizon in **one** transaction, so no query can see the history
without the partition while the horizon still allows it:

<<< @/snippets/Retention.cs#detach-partition

The cutoff is the partition's upper bound. `PruneHistoryAsync` then only deletes the few rows left in
other partitions (such as the default one). Keep the detached table instead of dropping it if you want
an archive.

### Caveats

- **Renaming a partitioned history table** needs a migration you edit by hand. EF Core renames the
  primary key by dropping it and adding it back on `history_id` alone, which PostgreSQL rejects on a
  partitioned table: its key must include `valid_to`. Keep the `ADD PRIMARY KEY` on
  `(history_id, valid_to)` in that migration.
- The version index and the key are created by the script, not by Hindsight. If you partition a
  history table of an entity with a composite key, list all the key columns in the version index.
