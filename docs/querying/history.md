---
order: 30
sidebarTitle: History<T> — versions with metadata
---

# Versions with metadata: `History<T>`

*Who changed this policy, when, and why?*

<<< @/snippets/Querying.cs#history

`History<Policy>()` returns every stored version wrapped in a `Version<Policy>`: the entity snapshot
plus the version's period and change context. It is an extension on the `DbContext`, not on a `DbSet`,
so you name the entity type.

| member | value |
|---|---|
| `Entity` | the `Policy` as it was in this version |
| `ValidFrom`, `ValidTo` | the half-open period `[ValidFrom, ValidTo)`, as UTC `DateTimeOffset` |
| `IsCurrent` | `true` for the version that is current now (`ValidTo` is `DateTimeOffset.MaxValue`) |
| `Operation` | `VersionOperation.Insert`, `Update` or `Delete` |
| `ChangedBy`, `ChangedByName`, `CorrelationId`, `Reason`, `Extra` | the [change context](/writing/change-context), or `null` |
| `DbSessionUser` | PostgreSQL's own `session_user` (opt-in, see [Database session user](/writing/change-context#database-session-user)), or `null` if the entity did not opt in |

## Deletes

Unlike `AllVersions`, `History<T>` **includes tombstones**. A delete comes back as a version with
`Operation == VersionOperation.Delete` and an empty period (`ValidFrom == ValidTo`), carrying the last
values and the change context of the delete. That makes it the delete audit:

<<< @/snippets/Querying.cs#deletions

## Composing

Rows come back newest first, by `ValidFrom` descending and then in insertion order; your own
`OrderBy` replaces that. `Where` and `Select` translate to SQL through both the metadata members and
the snapshot (`v.Entity.Id`, `v.Entity.Status`), with no client-side evaluation.

::: details The generated SQL
For a filter on `Entity.Status` (from Hindsight's test suite):

<<< @/../tests/Hindsight.IntegrationTests/HistoryQueryTests.History_generates_the_expected_sql.verified.sql
:::

The same [rules](/querying/restrictions) as for `AsOf` and `AllVersions` apply, including to
`Version<T>.Entity`: it is read-only.
