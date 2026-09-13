---
order: 10
sidebarTitle: AsOf — point in time
---

# Point in time: `AsOf`

*What did this policy look like when the claim was filed?*

<<< @/snippets/Querying.cs#as-of

`AsOf(instant)` reads from the history table instead of the main table, and keeps the version whose
period contains `instant`:

```sql
valid_from <= @instant AND valid_to > @instant
```

That is the version the database held at that moment. A policy that did not exist yet at `instant`,
or had already been deleted, returns nothing.

## Composing

Everything after `AsOf` composes as usual and becomes **one** SQL query:

<<< @/snippets/Querying.cs#as-of-composed

The instant is a query parameter, so queries at different instants share one compiled query and one
query plan.

::: details The generated SQL
For a filter on `Status` and a descending order on `Number` (from Hindsight's test suite):

<<< @/../tests/Hindsight.IntegrationTests/AsOfQueryTests.AsOf_generates_the_expected_sql.verified.sql
:::

## Rules

- `AsOf` must be the **first** operator, directly on the `DbSet`: `db.Policies.AsOf(t).Where(…)`,
  not `db.Policies.Where(…).AsOf(t)`.
- The result is no-tracking and read-only.
- `Include` throws.

See [Rules for historical queries](/querying/restrictions) for the details and the alternatives.
