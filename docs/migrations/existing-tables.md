---
order: 50
---

# Existing tables and upgrades

## Making an existing table temporal

When you call `IsTemporal()` on an entity whose table already has rows, the migration creates an
**empty** history table. Hindsight does not seed it. Until a row changes for the first time, it has
no version: `AsOf` returns nothing for it, and its history starts at that first change.

To give every existing row an initial version, seed the history table in the same migration, after it
is created, with `migrationBuilder.Sql(…)`:

```sql
INSERT INTO policies_history (id, number, status, premium, effective_from, valid_from, valid_to, operation)
SELECT id, number, status, premium, effective_from, now(), 'infinity', 1
FROM policies;
```

- List every versioned column, using your actual column names; leave out
  [excluded](/configuration/temporal-entities#excluding-properties) ones.
- `valid_from = now()` means "known since the migration": `AsOf` before that instant still returns
  nothing, which is the truth.
- `operation = 1` records it as an insert. The change-context columns stay `NULL`.

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
