---
order: 20
---

# Getting started

This page takes an EF Core application on PostgreSQL to its first historical query. The examples use
the `Policy` entity from the repository's
[sample project](https://github.com/horesse/hindsight-efcore/tree/master/samples/InsuranceSample):

<<< @/../samples/InsuranceSample/Policy.cs

## 1. Install the package

```bash
dotnet add package Hindsight.EntityFrameworkCore.PostgreSQL
```

## 2. Mark the entity as temporal

<<< @/snippets/AppDbContext.cs#mark-temporal

## 3. Enable Hindsight on the context

<<< @/snippets/GettingStarted.cs#register

`UseHindsight` adds a history table to the model for every temporal entity and installs the *history
writer* that fills it. `HistoryWriter.Trigger` writes history with a database trigger, which also
catches changes that bypass `SaveChanges`. It is the recommended writer; see
[Choosing a history writer](/writing/history-writers).

::: warning Choose the writer explicitly
`UseHindsight()` on its own uses `HistoryWriter.Interceptor`, which records only changes made through
`SaveChanges`. Pass `UseHistoryWriter(HistoryWriter.Trigger)` unless your database role cannot create
functions and triggers.
:::

## 4. Add a migration

```bash
dotnet ef migrations add MakePoliciesTemporal
dotnet ef database update
```

For a `policies` table, the migration creates:

- `policies_history`, with the entity's columns plus the period columns `valid_from` / `valid_to` and
  the change-context columns (see the [full column list](/migrations/generated-schema#the-history-table));
- two indexes on it that historical queries use;
- with the trigger writer, a `policies_history_write()` function and a `policies_history_trg`
  trigger on `policies`.

## 5. Save changes as usual

<<< @/snippets/GettingStarted.cs#write

Nothing about writing changes. Each `SaveChanges` that touches a temporal entity closes the current
version and opens a new one:

| version | `status` | `valid_from` | `valid_to` | `operation` |
|---|---|---|---|---|
| 1 | `Draft` | 09:00:00 | 09:05:00 | 1 (insert) |
| 2 | `Active` | 09:05:00 | `infinity` | 2 (update) |

## 6. Read the history

<<< @/snippets/GettingStarted.cs#query

Historical queries are always no-tracking, and their results cannot be saved back.

## Next steps

- [Concepts](/introduction/concepts): versions, periods and tombstones, in five minutes.
- [Tutorial: an audit trail for insurance policies](/tutorials/audit-trail): record who changed what,
  through an ASP.NET Core API.
- [Change context](/writing/change-context): store the user, the correlation id and the reason with
  every version.
