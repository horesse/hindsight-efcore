---
order: 25
sidebarTitle: FromTo / ContainedIn — time ranges
---

# Time ranges: `FromTo` and `ContainedIn`

*Show me every state this policy was in during Q3.*

<<< @/snippets/Querying.cs#from-to

`AsOf` answers a question about one instant, and `AllVersions` returns the whole timeline. The
questions in between are about a **window**: every version that was valid at some point in it, or
every version that started and ended inside it. `FromTo` and `ContainedIn` answer those. They mirror
SQL Server's `FOR SYSTEM_TIME FROM … TO …` and `CONTAINED IN (…)`.

Both take a window `[from, to)`. Like every period in Hindsight, it is half-open: it includes `from`
and excludes `to`.

| operator | returns a version `[valid_from, valid_to)` when | use it for |
|---|---|---|
| `FromTo(from, to)` | it overlaps the window: `valid_from < to` and `valid_to > from` | "every state during Q3", "active at any point this month" |
| `ContainedIn(from, to)` | it lies inside the window: `valid_from >= from` and `valid_to <= to` | "states that began and were replaced within the period" |

## Boundaries

With the timeline `Draft [08:00, 09:00)`, `Active [09:00, 10:00)` and `Cancelled [10:00, ∞)`:

| call | returns | why |
|---|---|---|
| `FromTo(08:30, 09:30)` | Active, Draft | both were valid at some instant of the window |
| `FromTo(09:00, 09:30)` | Active | Draft ended exactly at `from`, so it was not valid at any instant of the window |
| `FromTo(08:00, 09:00)` | Draft | Active started exactly at `to`, which the window excludes |
| `FromTo(11:00, 12:00)` | Cancelled | the current version overlaps every window after it started |
| `ContainedIn(08:00, 10:00)` | Active, Draft | a version may start exactly at `from` and end exactly at `to` |
| `ContainedIn(08:01, 10:00)` | Active | Draft started before the window |
| `ContainedIn(08:00, 12:00)` | Active, Draft | Cancelled has not ended, so it is not inside any bounded window |

- **An empty window returns nothing.** `FromTo(t, t)` contains no instant, so no version overlaps it.
  To read the state at one instant, use [`AsOf(t)`](/querying/as-of). (SQL Server's `FROM t TO t`
  behaves differently here and returns versions that strictly span `t`.)
- **`from` later than `to` throws** `ArgumentOutOfRangeException` when you call the operator.
- **`DateTimeOffset.MaxValue` means no upper bound.** It becomes PostgreSQL's `'infinity'`, so
  `ContainedIn(from, DateTimeOffset.MaxValue)` also returns the current version.
- Bounds with any offset are converted to UTC.

## Deletes, order and composing

They behave like [`AllVersions`](/querying/all-versions) restricted to a window:

- **Deletes are not versions.** A tombstone has an empty period, and PostgreSQL treats an empty range
  as inside every range, so without a filter `ContainedIn` would return every delete in the window as a
  duplicate of the last state. Both operators exclude tombstones. To see deletes, use
  [`History<T>`](/querying/history).
- Rows come back **newest first**, by `valid_from` descending. Your own `OrderBy` replaces that order.
- The operator must be the first one on the `DbSet`. Everything after it composes into one SQL
  statement:

<<< @/snippets/Querying.cs#from-to-composed

`ContainedIn` works the same way:

<<< @/snippets/Querying.cs#contained-in

::: details The generated SQL
For a filter on `Status` (from Hindsight's test suite). `FromTo` uses the range overlap operator `&&`:

<<< @/../tests/Hindsight.IntegrationTests/TimeRangeQueryTests.FromTo_generates_the_expected_sql.verified.sql

`ContainedIn` uses the range containment operator `<@`:

<<< @/../tests/Hindsight.IntegrationTests/TimeRangeQueryTests.ContainedIn_generates_the_expected_sql.verified.sql
:::

The predicate is written on `tstzrange(valid_from, valid_to)`, the same expression as the
[period-range index](/migrations/generated-schema#indexes) on every history table, so PostgreSQL can use that
index instead of scanning the whole history table. Both bounds are query parameters, so every window
reuses one compiled query.

## A window over `History<T>`

`History<T>` has no range operator; filter its `ValidFrom` and `ValidTo` instead. For example, every
change made during a window (including deletes) is the set of rows whose `ValidFrom` falls inside it:

<<< @/snippets/Querying.cs#history-in-window

This filter compares the period columns directly and does not use the period-range index. The
[rules for historical queries](/querying/restrictions) apply to `FromTo` and `ContainedIn` as well.
