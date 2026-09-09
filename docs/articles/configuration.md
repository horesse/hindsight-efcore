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
`IsTemporal()` entity. This is the form available today; the change-context and writer options below
are planned.

## Context configuration

> [!NOTE]
> Not implemented yet. The shape below is the v1 target.

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
- a temporal entity takes part in an inheritance hierarchy (not supported in v1).
