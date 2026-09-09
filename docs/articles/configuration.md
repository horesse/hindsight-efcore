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

## Context configuration

```csharp
options.UseHindsight(h => h
    .WithChangeContext<MyChangeContextProvider>()
    .UseHistoryWriter(HistoryWriter.Trigger));
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
- a temporal entity is part of a TPH hierarchy (not supported in v1).
