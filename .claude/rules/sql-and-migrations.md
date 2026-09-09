---
paths:
  - "src/**/Migrations/**"
  - "src/**/Sql/**"
  - "src/**/*Trigger*"
  - "src/**/*Migration*"
  - "samples/**/Migrations/**"
---

# Rules for SQL, triggers and migrations

- Target: PostgreSQL 14+. No syntax newer than 14 without a feature check and a documented fallback.
- Everything is `timestamptz`. Period predicate is always `valid_from <= $1 AND valid_to > $1`.
- Current version marker is `'infinity'::timestamptz`. Never `NULL`.
- Closing a version in the trigger: `valid_to = GREATEST(now(), OLD_valid_from + interval '1 microsecond')`
  so concurrent transactions cannot produce a negative or zero-length interval.
- Trigger functions are `CREATE OR REPLACE FUNCTION ... LANGUAGE plpgsql`, named
  `<history_table>_write()`, trigger named `<history_table>_trg`, `AFTER INSERT OR UPDATE OR DELETE
  FOR EACH ROW`. Re-generated whenever the versioned column set changes.
- Change context is read with `current_setting('hindsight.<key>', true)` — the `true` (missing_ok)
  is mandatory; a transaction without context must still succeed.
- History tables never get: foreign keys, unique constraints, check constraints, `NOT NULL` on
  entity columns, or a `DROP COLUMN` from a generated migration.
- Required indexes on every history table: `(<pk columns>, valid_from DESC)` and
  `GIST (tstzrange(valid_from, valid_to))`. A history table without them is a bug, not a config option.
- Identifiers are quoted through EF Core's `ISqlGenerationHelper.DelimitIdentifier`; never hand-quote.
- Any change here is verified with a Verify DDL snapshot *and* an integration test that applies the
  migration to a fresh database and inspects `information_schema`.
