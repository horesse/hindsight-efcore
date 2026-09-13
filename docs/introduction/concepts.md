---
order: 30
---

# Concepts

## System time vs. application time

Two different questions hide behind the word "temporal":

- **System time**: *when did the database hold this row?* It is recorded automatically. Every
  insert, update and delete opens a new version and closes the previous one. **This is what Hindsight
  tracks.**
- **Application time**, also called valid time: *when was this fact true in the real world?* For
  example, "this tariff applies from March 1". The application chooses the dates. PostgreSQL 19
  supports it natively with `FOR PORTION OF` and `WITHOUT OVERLAPS`. Hindsight does not implement it;
  the API is shaped so it can be added later without breaking changes.

## Versions and periods

Each row in a history table is a **version**: a snapshot of the entity's versioned columns, plus the
**period** during which it was the current row.

- Periods are **half-open**: `[valid_from, valid_to)` includes `valid_from` and excludes `valid_to`.
  When one version closes at 09:05, the next opens at exactly 09:05, with no gap and no overlap.
- Both columns are `timestamptz`, in UTC.
- The current version has `valid_to = 'infinity'`, not `NULL`: it compares and indexes correctly.

So "the version at instant `t`" is always one predicate, `valid_from <= t AND valid_to > t`, and it
matches at most one row per entity.

Here is a policy created at 09:00, activated at 09:05 and deleted at 10:00:

| `operation` | `status` | `valid_from` | `valid_to` | |
|---|---|---|---|---|
| 1 (insert) | `Draft` | 09:00 | 09:05 | |
| 2 (update) | `Active` | 09:05 | 10:00 | |
| 3 (delete) | `Active` | 10:00 | 10:00 | tombstone |

## Tombstones

A delete closes the open version and writes a **tombstone**: a row with the last values and an empty
period (`valid_from = valid_to`). It never matches a point-in-time query, so after 10:00 `AsOf` finds
nothing, but it records *when* the policy was deleted and *who* did it.
[`History<T>`](/querying/history) returns tombstones; [`AsOf`](/querying/as-of) and
[`AllVersions`](/querying/all-versions) do not.

## One timestamp per transaction

All history rows written by one `SaveChanges` share one instant, so a change that spans several
entities is consistent at every point in time. The trigger writer uses PostgreSQL's `now()`, the
transaction timestamp; the interceptor writer takes one value from `TimeProvider` per `SaveChanges`.

## Two tables, not a flag

The main table stays as it is. History is a separate table:

- **without foreign keys** back to the main table, because a deleted row's history must survive it;
- **without the original's `NOT NULL`, unique or check constraints**, because a unique index on a
  history table breaks on the second edit.

It contains every mapped property of the entity except the ones you
[exclude](/configuration/temporal-entities#excluding-properties).

## Change context

The database knows that a row changed. Only the application knows who was acting, on behalf of which
request, and why. Hindsight stores that as a `ChangeContext` (user id, user name, correlation id,
reason and JSON extras) on every version. See [Change context](/writing/change-context).

## History writers

Something has to turn each change into history rows. Hindsight can do it from a trigger in the
database or from an interceptor in the application. Both produce the same history table, so switching
is one migration. See [Choosing a history writer](/writing/history-writers).

## Historical results are read-only

Everything a historical query returns is a detached snapshot. Saving one back would rewrite the
present with the past, so Hindsight throws instead. See
[Rules for historical queries](/querying/restrictions).
