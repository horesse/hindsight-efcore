---
order: 20
sidebarTitle: AllVersions — every version
---

# Every version: `AllVersions`

*Show me this policy's whole history.*

<<< @/snippets/Querying.cs#all-versions

`AllVersions()` reads from the history table and returns **every stored version**: one per insert and
one per update. There is no period filter; this is the full timeline, not a point in time.

- Rows come back **newest first**, by `valid_from` descending. Your own `OrderBy` or
  `OrderByDescending` replaces that order; a trailing `ThenBy` keeps newest first as the primary order.
- **Deletes are not versions.** A deleted policy's timeline ends at the version that was current when
  it was deleted. To see the delete itself, when and by whom, use [`History<T>`](/querying/history).

## Composing

Like `AsOf`, it must be the first operator on the query, and everything after it composes into one SQL
statement:

<<< @/snippets/Querying.cs#all-versions-composed

::: details The generated SQL
For a filter on `Status` (from Hindsight's test suite). `operation <> 3` excludes tombstones:

<<< @/../tests/Hindsight.IntegrationTests/AllVersionsQueryTests.AllVersions_generates_the_expected_sql.verified.sql
:::

`AllVersions` returns entity snapshots only. When you also need each version's period or change
context, use [`History<T>`](/querying/history).
