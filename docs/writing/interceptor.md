---
order: 30
---

# Interceptor writer

`HistoryWriter.Interceptor` writes history from a `SaveChangesInterceptor` in your application, in the
same transaction as the change. It is the default writer. Use it where the role that applies
migrations cannot create functions and triggers; otherwise prefer the
[trigger writer](/writing/trigger).

::: warning It only sees `SaveChanges`
`ExecuteUpdate`, `ExecuteDelete`, writes through `FromSql` or `SqlQuery`, and changes made by other
processes never reach a `SaveChangesInterceptor`. They write **no history** under this writer.
:::

## How a `SaveChanges` is recorded

1. **Before the save**, the interceptor snapshots every temporal entity that is `Added`, `Modified` or
   `Deleted`, and drops any `Modified` entity whose only changes are to
   [excluded](/configuration/temporal-entities#excluding-properties) properties. It takes one timestamp
   from `TimeProvider` for the whole save, and opens a transaction if you have none (see
   [Transactions](/writing/transactions)).

   For each `Deleted` entity it re-reads the row from the database by key (`SELECT … FOR UPDATE`).
   An entity deleted without being loaded first, such as `Remove(new Policy { Id = id, … })`, only
   holds default values, and those must not end up in the tombstone. If the row no longer exists, the
   save throws `InvalidOperationException`.
2. **The save runs** as usual.
3. **After the save**, when store-generated keys are known, it writes history for every entity in a
   single batched round trip (split every 512 rows):
   - `Added`: inserts an open version;
   - `Modified`: closes the open version and inserts the new one;
   - `Deleted`: closes the open version and inserts a tombstone.
4. It commits the transaction if it opened it.

The open version is closed at `GREATEST(@timestamp, valid_from + interval '1 microsecond')`, the same
clamp as the trigger writer. A save that captured its timestamp and then waited for a row lock, or a
clock that stepped backwards, still produces a contiguous chain of positive periods. In exactly those
cases, the new version starts slightly later than the time `TimeProvider` reported.

Values are sent as parameters with each column's own type mapping, so an enum stored as text, a
`jsonb` column or a `numeric(18,4)` column lands in history exactly as in the main table.

## If the history write fails {#if-the-history-write-fails}

The history is written after EF Core has sent the change *and* accepted it in the change tracker. If
the history write then fails, for example because of invalid JSON in `ChangeContext.Extra` or a
dropped connection:

- **The database is always correct.** The interceptor rolls back its transaction, so neither the
  change nor its history is stored.
- **`Added` and `Modified` entities are restored** to their state before the save, so catching the
  exception and calling `SaveChanges` again retries the whole change.
- **`Deleted` entities stay detached.** EF Core has already removed them from the change tracker, so a
  retry does nothing for them. Query the entity again and remove it again.
- **An `Added` entity with a store-generated key keeps the key** it received in the rolled-back
  transaction. See [Limitations](/reference/limitations#known-trade-offs) for what a retry does with it.

The trigger writer does not have this gap: its history is written in the same statement as the change.

## Your own interceptors run after Hindsight's {#your-own-interceptors-run-after-hindsights}

A `SaveChangesInterceptor` you add with `AddInterceptors(...)` always runs **after** Hindsight's, no
matter where it appears relative to `UseHindsight(...)`. That is EF Core's ordering, not a setting.

So if your interceptor changes an entity's state inside `SavingChanges`, for example an audit stamp
that flips an `Unchanged` entity to `Modified`, Hindsight has already taken its snapshot. The update
reaches the main table, but **no version is written** for it, and no exception is thrown.

If you rely on such an interceptor, use the trigger writer: the trigger sees the `UPDATE` in the
database, whoever caused it.

## Cost

About 1.7× to 3× the time of a plain `SaveChanges`, depending on the operation, and one extra round
trip for a save that deletes a temporal entity. See the [benchmarks](/reference/benchmarks).
