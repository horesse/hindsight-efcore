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
options.UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger));
```

`HistoryWriter.Interceptor` (the default, so passing it is optional) writes history from a
`SaveChangesInterceptor` in the application, in the same transaction as the data change, with one
timestamp per `SaveChanges` taken from the registered `TimeProvider`. `HistoryWriter.Trigger` moves
that logic into a plpgsql trigger the migration generates, so it also captures `ExecuteUpdate`,
`ExecuteDelete` and raw SQL, and uses `now()` for the timestamp; it is recommended in production. Both
produce the same history schema, so switching is one migration. See
[History writers](history-writers.md) for the trade-offs and the exact mechanics.

To make the interceptor's timestamp deterministic in tests, register a `TimeProvider` on the
application service provider (`services.AddSingleton<TimeProvider>(new FakeTimeProvider())`); it is
resolved from there and falls back to `TimeProvider.System`. The trigger writer uses `now()` and
ignores `TimeProvider`; assert on interval shape instead, or use distinct transactions.

## Context configuration

Every history row has five context columns — `changed_by`, `changed_by_name`, `correlation_id`,
`reason` and `extra` (`jsonb`) — that record *who* made a change and *why*. They are written `NULL`
unless you register an <xref:Hindsight.IChangeContextProvider>.

```csharp
public sealed class HttpChangeContextProvider(IHttpContextAccessor accessor) : IChangeContextProvider
{
    public ChangeContext GetChangeContext(DbContext context)
    {
        var user = accessor.HttpContext?.User;
        return new ChangeContext
        {
            UserId = user?.FindFirst("sub")?.Value,
            UserName = user?.Identity?.Name,
            CorrelationId = Activity.Current?.TraceId.ToString(),
            Extra = """{"source":"web"}""",
        };
    }
}
```

```csharp
services.AddHttpContextAccessor();
services.AddScoped<HttpChangeContextProvider>();

services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseHindsight(h => h
        .WithChangeContext<HttpChangeContextProvider>()
        .UseHistoryWriter(HistoryWriter.Interceptor)));
```

`WithChangeContext<T>` registers the provider type. It is called **once per `SaveChanges`** that
writes a temporal entity — never once per row. Under `HistoryWriter.Interceptor` the returned
<xref:Hindsight.ChangeContext> is stamped onto every history row that call's `INSERT` produces; under
`HistoryWriter.Trigger` it is pushed into the transaction with `set_config` and the trigger reads it
back. Either way the provider is resolved from the application service provider (so it can take
dependencies); a provider with a parameterless constructor is created directly. An exception from the
provider propagates out of `SaveChanges` and the whole transaction, data change included, rolls back.
Hindsight ships no default provider — `IHttpContextAccessor` / `ClaimsPrincipal` mapping like the
sample above is application code.

Under `HistoryWriter.Trigger`, `ExecuteUpdate` / `ExecuteDelete` and raw SQL still record history, but
their context columns are `NULL`: they do not go through `SaveChanges`, so no `ChangeContext` is
pushed for them.

Each `ChangeContext` member maps to one column: `UserId` → `changed_by`, `UserName` →
`changed_by_name`, `CorrelationId` → `correlation_id`, `Reason` → `reason`, `Extra` → `extra`. A
`null` member leaves that column `null`. `Extra` is written verbatim into the `jsonb` column, so it
must be valid JSON — the provider owns serialization.

A reason for a specific operation is set with a scope, overriding `ChangeContext.Reason` for the
`SaveChanges` calls inside it:

```csharp
using (db.WithReason("Backdated correction after audit"))
{
    policy.Premium = corrected;
    await db.SaveChangesAsync();
}
```

`WithReason` works with or without a provider registered; scopes nest and the innermost one wins.

## Model validation

Hindsight validates the model at build time and fails fast with a specific message when:

- a temporal entity has no primary key;
- the history table name collides with another table;
- a required property without a default is excluded;
- a temporal entity is an owned type;
- a temporal entity has an owned reference (`OwnsOne`) or a complex property — their columns live on
  their own type, not the owner's, so history can't mirror them (not supported in v1);
- a temporal entity takes part in an inheritance hierarchy (not supported in v1).
