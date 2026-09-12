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
and a `SaveChanges` that touched only excluded properties writes no history row. You can exclude some
primary-key properties of a composite key as long as at least one stays versioned — but excluding all
of them is rejected (see [Model validation](#model-validation)), since Hindsight would then have no
column left to identify which history rows belong to which version of the entity.

## Enabling Hindsight

```csharp
services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseHindsight());
```

`UseHindsight()` turns on the convention that builds a history table into the model for every
`IsTemporal()` entity, and installs the history writer that fills those tables on `SaveChanges`.

Hindsight only supports the Npgsql/PostgreSQL provider (see
[Limitations and non-goals](limitations.md#not-in-v1)). It checks for this itself: the first time the
context is used with any other provider configured (`UseSqlite(...)`, `UseSqlServer(...)`, and so on),
Hindsight fails fast with a clear `InvalidOperationException` naming the provider it found and the one
it needs, instead of letting you hit a confusing error later from EF Core's migrations or SQL
generation.

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

## EnableRetryOnFailure and transactions

Both writers open a transaction themselves when `SaveChanges` is called with none already open, so the
data change and the history row(s) commit together (see [History writers](history-writers.md)). That
transaction is opened from inside `SaveChanges` itself, which is a problem for
`UseNpgsql(cs, o => o.EnableRetryOnFailure())`: its retrying execution strategy can re-run the whole
`SaveChanges` call after a transient failure, and a transaction opened inside that call would not
survive the retry. EF Core (or Npgsql, for an ambient `TransactionScope`) refuses to let that happen —
so, with retry enabled and no transaction of your own, `SaveChanges` throws
`InvalidOperationException` naming the conflict and the fix, from whichever writer would have opened
the transaction:

```csharp
services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
    .UseHindsight());

// throws InvalidOperationException: this context is configured with both a retrying execution
// strategy and Hindsight's history writer, which needs its own transaction.
await db.SaveChangesAsync();
```

Fix it the way EF Core's own docs recommend for combining retry with a transaction: wrap the call in
`CreateExecutionStrategy().ExecuteAsync(...)` and open the transaction yourself inside it. Because your
transaction is then open before `SavingChanges` fires, Hindsight sees `CurrentTransaction` already set
and uses it instead of opening its own — no conflict, and the whole unit, data change and history rows
included, is safely retried together:

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    // ... modify tracked entities ...
    await db.SaveChangesAsync();
    await tx.CommitAsync();
});
```

If you never open your own transaction around a `SaveChanges` that writes a temporal entity, do not
combine `EnableRetryOnFailure()` with Hindsight. An ambient `System.Transactions.TransactionScope` is
not a fix either — it is not supported here at all, with or without retry enabled, because Hindsight
opening its own transaction inside one is exactly the case Npgsql and EF Core refuse (see
[Limitations → Known trade-offs](limitations.md#known-trade-offs)).

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
The scope is tied to `db` specifically — a `SaveChanges` on a different `DbContext` instance, even
one running inside the same `using` block, is never affected.

## Model validation

Hindsight validates the model at build time — the same point `dotnet ef migrations add` builds it at
— and fails fast with a specific message when:

- a temporal entity has no primary key;
- every property of a temporal entity's primary key is excluded from history with `Exclude(...)` —
  Hindsight would have no column left to identify which history rows belong to which version of the
  entity, so un-exclude at least one primary-key property (`Exclude(...)` is meant for noisy non-key
  columns, not the key itself);
- a temporal entity is not mapped to a table;
- a temporal entity has an owned reference (`OwnsOne`) or a complex property — their columns live on
  their own type, not the owner's, so history can't mirror them (not supported in v1);
- a temporal entity takes part in an inheritance hierarchy (not supported in v1);
- a source property's column collides with one of the fixed history columns (`history_id`,
  `operation`, `changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`) or with the
  entity's own period-start/period-end column — rename the property's column, exclude it, or (for a
  period-column collision) pick different period column names;
- the period-start and period-end column names are the same.

A history table name that collides with another table in the model is also rejected, but by EF
Core's own model validation rather than a Hindsight-specific message, since the history entity type
is an ordinary property-bag entity type as far as EF Core is concerned.
