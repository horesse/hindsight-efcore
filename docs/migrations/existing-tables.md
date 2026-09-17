---
order: 50
---

# Existing tables and upgrades

## Making an existing table temporal

When you call `IsTemporal()` on an entity whose table already has rows, the migration creates the
history table **and seeds it automatically**: right after `CREATE TABLE` (and, under the
[Trigger writer](/writing/history-writers), after the trigger), it appends

```sql
INSERT INTO policies_history (id, number, status, premium, effective_from, valid_from, valid_to, operation)
SELECT id, number, status, premium, effective_from, now(), 'infinity', 1
FROM policies;
```

— one seeded version per existing row, using every versioned column (leaving out
[excluded](/configuration/temporal-entities#excluding-properties) ones and a composite key's columns
appear like any other). `valid_from = now()` means "known since the migration": `AsOf` before that
instant still correctly returns nothing. `operation = 1` records it as an insert, like any other first
version. The change-context columns (`changed_by`, `changed_by_name`, `correlation_id`, `reason`,
`extra`, and the opt-in `db_session_user`) stay `NULL` — there is no change context to attribute a
migration-time seed to.

This runs unconditionally for every newly created history table, in both writer modes, whether the
entity is brand new (its main table is created empty in the same migration, so the `SELECT` returns zero
rows — a no-op) or, as here, an existing entity that just had `IsTemporal()` added. It runs exactly once,
next to the table's own creation; a table already made temporal keeps growing its history through the
ordinary writer path from then on, not through seeding again.

**You do not need to write this by hand anymore.** If you want a different `valid_from` (e.g., "these
rows have really been valid since their `created_at`, not since the migration ran") or want to exclude a
column differently than [`Exclude(...)`](/configuration/temporal-entities#excluding-properties) already
does, edit the generated migration's seeding statement, or write your own with `migrationBuilder.Sql(…)`
right after the generated one and delete the generated one — the shape above is exactly what you'd write
by hand:

- List every versioned column, using your actual column names; leave out excluded ones.
- `valid_from` is the instant you want `AsOf` to start returning the row; `valid_to = 'infinity'` marks it
  current.
- `operation = 1` records it as an insert. Leave the change-context columns `NULL` unless you have a real
  value to attribute the seed to.

If you're upgrading from a version of Hindsight that predates this (before the release that shipped
DESIGN.md D6's "existing non-empty table made temporal" bullet) and already hand-wrote this seeding SQL
per the guidance above, see [Upgrading Hindsight](/reference/upgrading) — your next such migration would
otherwise seed the same rows twice.

## Upgrading Hindsight

Upgrading the package is normally a no-op for your schema: run `dotnet ef migrations add` and expect
an empty migration. When a release needs something more, the [upgrade guide](/reference/upgrading)
says so, version by version.

### Backfilling the period-range index {#backfilling-the-period-range-index}

Since 1.1, every new history table gets a GiST index on its period,
`ix_<history_table>_period` (see [What a migration creates](/migrations/generated-schema#indexes)).
History tables created by 1.0 do not have it, and upgrading does not add it: the index is created only
next to the history table's `CREATE TABLE`, so for existing tables `dotnet ef migrations add`
scaffolds nothing.

Without it, a point-in-time query that is not also filtered by the entity's key scans the whole
history table. Add it yourself, in a hand-written migration or directly:

```sql
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_policies_history_period
    ON policies_history USING gist (tstzrange(valid_from, valid_to));
```

- Use `CONCURRENTLY`. A plain `CREATE INDEX` blocks writes to the history table, and so every write to
  the main table under the trigger writer, for as long as the build takes. `CONCURRENTLY` cannot run
  inside a transaction, so run it outside a migration transaction.
- Substitute your history table's name, and your period column names if you changed them with
  `HasPeriodStart` / `HasPeriodEnd`.
- Keep the name `ix_<history_table>_period`, so your schema matches what a freshly created table gets.
