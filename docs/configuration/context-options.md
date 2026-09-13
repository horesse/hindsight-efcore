---
order: 20
---

# Enabling Hindsight

`UseHindsight()` turns Hindsight on for a context:

<<< @/snippets/Configuration.cs#use-hindsight

It does two things:

- it adds a history table to the EF Core model for every `IsTemporal()` entity, so migrations create
  and evolve it;
- it installs the history writer that fills those tables.

## Options

<<< @/snippets/Configuration.cs#use-hindsight-configured

| option | default | see |
|---|---|---|
| `UseHistoryWriter(HistoryWriter)` | `HistoryWriter.Interceptor` | [Choosing a history writer](/writing/history-writers) |
| `WithChangeContext<TProvider>()` | none: the change-context columns are written `NULL` | [Change context](/writing/change-context) |

::: tip Use the trigger writer in production
The default writer is `HistoryWriter.Interceptor` for compatibility, but `HistoryWriter.Trigger` is
the recommended one: it also records `ExecuteUpdate`, `ExecuteDelete` and raw SQL.
:::

## Without dependency injection

`UseHindsight` works on any `DbContextOptionsBuilder`, including the generic one, which it returns
unchanged in type:

<<< @/snippets/Configuration.cs#options-builder

## PostgreSQL only

Hindsight supports the Npgsql provider only. The first time a context is used with Hindsight and any
other provider (`UseSqlite`, `UseSqlServer`, …), it throws an `InvalidOperationException` that names
the provider it found and the one it needs, rather than failing later in migrations or SQL generation.

## Timestamps and `TimeProvider`

The two writers take the time of a change from different places:

- `HistoryWriter.Interceptor` takes one timestamp per `SaveChanges` from the `TimeProvider` registered
  in the application's service provider, or `TimeProvider.System` if there is none. Register a fake
  one to make history timestamps deterministic in tests:

  <<< @/snippets/Configuration.cs#time-provider

- `HistoryWriter.Trigger` uses PostgreSQL's `now()` and ignores `TimeProvider`. In tests, assert on
  the shape of the periods (contiguous, half-open, one open version) or save in separate transactions.
