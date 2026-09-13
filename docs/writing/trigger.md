---
order: 20
---

# Trigger writer

`HistoryWriter.Trigger` writes history inside PostgreSQL, with a trigger on the main table. Because it
lives in the database, it sees every change to the table, whoever makes it: `SaveChanges`,
`ExecuteUpdate`, `ExecuteDelete`, raw SQL, or another process. It is the recommended writer.

<<< @/snippets/GettingStarted.cs#register

## What the migration creates

For every temporal entity, the migration that creates the history table also creates:

- a plpgsql function, `<history_table>_write()`;
- an `AFTER INSERT OR UPDATE OR DELETE … FOR EACH ROW` trigger, `<history_table>_trg`, on the main
  table.

Both are added when the migration is turned into SQL (`dotnet ef database update`,
`dotnet ef migrations script`); they do not appear in the migration's C# file. Use
`dotnet ef migrations script` to review them.

::: details The generated function and trigger for a `policies` table
Exact output from Hindsight's test suite, for a table with `id`, `number`, `premium` and `status`
columns:

<<< @/../tests/Hindsight.IntegrationTests/TriggerDdlTests.Trigger_ddl_has_the_expected_shape.verified.sql
:::

## What it does

| statement | history |
|---|---|
| `INSERT` | inserts an open version: `valid_from = now()`, `valid_to = 'infinity'`, `operation = 1` |
| `UPDATE` | if no versioned column changed, nothing; otherwise closes the open version and inserts a new one that starts exactly where it ended, `operation = 2` |
| `DELETE` | closes the open version and inserts a tombstone with an empty period, `operation = 3` |

The open version is closed at `GREATEST(now(), valid_from + interval '1 microsecond')`. `now()` is the
transaction's start time, so two transactions racing on one row could otherwise close a version before
it began; the clamp keeps every period strictly positive and the chain contiguous.

Because `now()` is fixed for the whole transaction, every history row a transaction writes carries the
same instant.

## Change context

The trigger cannot see your application, so Hindsight hands it the
[change context](/writing/change-context) through transaction-local settings. Once per `SaveChanges`
that writes a temporal entity, it runs:

```sql
SELECT set_config('hindsight.changed_by', @changedBy, true),
       set_config('hindsight.correlation_id', @correlationId, true) /* …and the other three */;
```

and the trigger reads each value back with `current_setting('hindsight.changed_by', true)`.

- A transaction-local setting needs a transaction, so if you have none open, Hindsight opens one for
  that `SaveChanges` and commits it with the data. It only does this when there is a context to push,
  meaning a provider is registered or `WithReason` is active. See [Transactions](/writing/transactions).
- Inside a transaction you opened yourself, Hindsight pushes all five values on every `SaveChanges`,
  empty ones included, so a later save never inherits an earlier one's reason.
- `ExecuteUpdate`, `ExecuteDelete` and raw SQL do not go through `SaveChanges`. The trigger still
  records them, with `NULL` change-context columns.
- The push costs one round trip per `SaveChanges`, about 3 ms on a 100-row update.

Anyone who can connect to the database can set the same values before writing. See the
[trust model](/writing/change-context#trust-model).

## Schema changes

When a later migration adds, removes or renames a column of a temporal entity, it also replaces the
function (`CREATE OR REPLACE FUNCTION`) so that it writes the current set of columns. The trigger
definition itself does not change. See [Evolving a temporal entity](/migrations/schema-evolution).

## Privileges

The role that applies migrations needs to own the main table, to create the trigger, and to have
`CREATE` on the schema, to create the function. No superuser, no extension. If policy forbids that, use
the [interceptor writer](/writing/interceptor).

## Failures

The trigger writes history in the same statement as the change. Either both succeed or the statement
fails and `SaveChanges` throws before EF Core accepts any change, so there is nothing to recover.
