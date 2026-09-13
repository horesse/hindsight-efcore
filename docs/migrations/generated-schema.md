---
order: 10
---

# What a migration creates

For every temporal entity, the migration that first includes it creates a history table, two indexes
on it and, with the trigger writer, a trigger. Later migrations keep them in step with the entity; see
[Evolving a temporal entity](/migrations/schema-evolution).

## The history table

Named `<main table>_history` in the main table's schema, unless you
[choose another name](/configuration/temporal-entities#customizing).

| column | type | |
|---|---|---|
| `history_id` | `bigint generated always as identity` | the history table's own primary key |
| every versioned column | as in the main table, but nullable | [excluded](/configuration/temporal-entities#excluding-properties) properties are left out |
| `valid_from` | `timestamptz not null` | start of the version's period, included |
| `valid_to` | `timestamptz not null` | end of the period, excluded; `'infinity'` for the current version |
| `operation` | `smallint` | `1` insert, `2` update, `3` delete |
| `changed_by`, `changed_by_name`, `correlation_id`, `reason` | `text null` | the [change context](/writing/change-context) |
| `extra` | `jsonb null` | the change context's JSON extras |

The history table has **no foreign keys** back to the main table, because a deleted row's history must
outlive it, and **none of the main table's `NOT NULL`, unique or check constraints**, because a unique
index on a history table breaks on the second edit of a row. Don't add them by hand.

## Indexes

| index | definition | serves |
|---|---|---|
| `ix_<history_table>_version` | btree on `(<primary key columns>, valid_from desc)` | historical queries filtered by the entity's key |
| `ix_<history_table>_period` | `gist (tstzrange(valid_from, valid_to))` | point-in-time queries that are not filtered by key |

The period index needs no extension: range types have a GiST operator class in PostgreSQL core. Both
indexes are created once, with the table, and never changed by a later migration. History tables
created before 1.1 lack the period index; see
[Backfilling the period-range index](/migrations/existing-tables#backfilling-the-period-range-index).

## The trigger

With `HistoryWriter.Trigger`, the same migration also creates the `<history_table>_write()` function
and the `<history_table>_trg` trigger on the main table. See [Trigger writer](/writing/trigger).

## Where the DDL comes from

| object | in the migration's C# | added when the migration becomes SQL |
|---|---|---|
| history table, version index | ✅ they are part of the EF Core model | |
| period index | | ✅ |
| trigger function and trigger | | ✅ |

Hindsight adds the SQL-only objects when EF Core turns a migration into SQL, which is what
`dotnet ef database update`, `dotnet ef migrations script`, migration bundles and
`Database.Migrate()` all do. Two consequences:

- Review a migration with `dotnet ef migrations script`, not only by reading its C# file.
- The context that **applies** migrations must be configured with `UseHindsight()` and the same
  `UseHistoryWriter(...)` as your application. For the `dotnet ef` tools that is your design-time
  context: the one your `IDesignTimeDbContextFactory` creates, or your application's own registration.

## Generated names

| object | name |
|---|---|
| history table | `<main table>_history` |
| version index | `ix_<history_table>_version` |
| period index | `ix_<history_table>_period` |
| trigger function | `<history_table>_write()` |
| trigger | `<history_table>_trg` |

Every name must fit PostgreSQL's 63-byte identifier limit; Hindsight checks that when it builds the
model. See [Model validation](/configuration/model-validation).
