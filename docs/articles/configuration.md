# Configuration

## Entity configuration

```csharp
modelBuilder.Entity<Policy>().IsTemporal();
```

Defaults: history table `<table>_history` in the same schema, period columns `valid_from` / `valid_to`,
every mapped property versioned.

```csharp
modelBuilder.Entity<Risk>().IsTemporal(t => t
    .UseHistoryTable("risks_history", schema: "audit")
    .HasPeriodStart("sys_from")
    .HasPeriodEnd("sys_to")
    .Exclude(r => r.RecalculatedAt));
```

`Exclude` removes a property from versioning entirely: the column is not copied to the history table,
and a `SaveChanges` that touched only excluded properties writes no history row.

## Enabling Hindsight

```csharp
services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseHindsight());
```

`UseHindsight()` turns on the convention that builds a history table into the model for every
`IsTemporal()` entity, and installs the history writer that fills those tables on `SaveChanges`.

## Choosing the history writer

```csharp
options.UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Interceptor));
```

`HistoryWriter.Interceptor` (the default, so the call above is optional) writes history from a
`SaveChangesInterceptor` in the application, in the same transaction as the data change, with one
timestamp per `SaveChanges` taken from the registered `TimeProvider`. `HistoryWriter.Trigger` — a
database trigger that also captures `ExecuteUpdate` and raw SQL — is not implemented yet and throws
`NotSupportedException`. See [History writers](history-writers.md) for the trade-offs and the exact
mechanics.

To make the timestamp deterministic in tests, register a `TimeProvider` on the application service
provider (`services.AddSingleton<TimeProvider>(new FakeTimeProvider())`); the interceptor resolves it
from there and falls back to `TimeProvider.System`.

## Context configuration

> [!NOTE]
> Not implemented yet. The shape below is the v1 target. The `changed_by`, `changed_by_name`,
> `correlation_id`, `reason` and `extra` history columns are written `NULL` until it ships.

```csharp
options.UseHindsight(h => h
    .WithChangeContext<MyChangeContextProvider>()
    .UseHistoryWriter(HistoryWriter.Interceptor));
```

`WithChangeContext<T>` registers an `IChangeContextProvider`. The default provider reads
`ClaimsPrincipal` from `IHttpContextAccessor` when it is registered and `Activity.Current?.TraceId`
for the correlation id.

A reason for a specific operation is set with a scope:

```csharp
using (db.WithReason("Backdated correction after audit"))
{
    policy.Premium = corrected;
    await db.SaveChangesAsync();
}
```

## Model validation

Hindsight validates the model at build time and fails fast with a specific message when:

- a temporal entity has no primary key;
- the history table name collides with another table;
- a required property without a default is excluded;
- a temporal entity is an owned type;
- a temporal entity takes part in an inheritance hierarchy (not supported in v1).
