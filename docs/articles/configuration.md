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

Both writers open a transaction themselves when `SaveChanges` is called with none already open (and no
ambient `TransactionScope` either — see [Ambient TransactionScope](#ambient-transactionscope) below), so
the data change and the history row(s) commit together (see [History writers](history-writers.md)). That
transaction is opened from inside `SaveChanges` itself, which is a problem for
`UseNpgsql(cs, o => o.EnableRetryOnFailure())`: its retrying execution strategy can re-run the whole
`SaveChanges` call after a transient failure, and a transaction opened inside that call would not
survive the retry. EF Core refuses to let that happen — so, with retry enabled and no transaction of
your own, `SaveChanges` throws `InvalidOperationException` naming the conflict and the fix, from
whichever writer would have opened the transaction:

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
combine `EnableRetryOnFailure()` with Hindsight — not even by wrapping the call in an ambient
`TransactionScope` instead of `CreateExecutionStrategy()`. A retrying execution strategy refuses to run
inside an ambient `TransactionScope` at all (a transient failure could not be retried inside a
transaction that already exists), regardless of whether Hindsight would have opened a transaction of its
own — `SaveChanges` throws EF Core's own `InvalidOperationException` ("does not support user-initiated
transactions") before either writer's `SavingChanges` hook even runs.

## Ambient TransactionScope

Without `EnableRetryOnFailure()`, an ambient `System.Transactions.TransactionScope` opened around a
`SaveChanges` that has no transaction of its own works, in both writer modes:

```csharp
using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
db.Policies.Add(new Policy { /* ... */ });
await db.SaveChangesAsync();
scope.Complete();
```

Npgsql enlists the connection in the ambient transaction automatically the moment it opens, and every
command Hindsight or EF Core runs on that connection afterwards — the data write, the trigger writer's
`set_config` push, the history `INSERT` — rides that enlistment. Opening a second, EF-managed transaction
on top of an already-enlisted connection is exactly what EF Core refuses
(`InvalidOperationException: An ambient transaction has been detected...`), so when
`context.Database.CurrentTransaction` is `null` and `System.Transactions.Transaction.Current` is not,
Hindsight opens no transaction of its own — it opens the connection explicitly instead (so the
enlistment happens immediately) and lets the ambient scope own commit and rollback entirely: calling
`scope.Complete()` commits the data change and the history row(s) together, and letting the `using` block
dispose without calling it rolls both back together, exactly as with a transaction Hindsight opens
itself. This holds whether `SaveChanges` is the first operation to touch the connection in the scope or
a query already ran on it first, and whether the connection was previously used (and closed) before the
scope even started — Npgsql re-enlists a connection into whichever transaction is ambient the next time
it opens, pooled or not.

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

## Pooled and factory-created contexts

`AddDbContextPool<T>()`, `AddDbContextFactory<T>()` and `AddPooledDbContextFactory<T>()` all build one
`DbContextOptions` instance once, when the pool or factory is configured, and every pooled or
factory-created `DbContext` instance shares it. `CoreOptionsExtension.ApplicationServiceProvider` —
the provider Hindsight resolves `IChangeContextProvider` from — is baked into that shared instance at
the moment it is built, which is normally the application's **root** container, not any request's
scope. Confirmed against EF Core 10.0.12: it is identical across every simulated request that rents
from the same pool or factory, unlike plain `AddDbContext<T>()`, where each request gets its own
`DbContextOptions` built from that request's own scope.

A `HttpChangeContextProvider` registered `Scoped`, exactly as in the example above, cannot be resolved
correctly from a captured root provider:

- With `ServiceProviderOptions.ValidateScopes` on — ASP.NET Core's Development default — every
  `SaveChanges` that writes a temporal entity throws immediately:
  `InvalidOperationException: Failed to resolve change context provider '...'. ... Cannot resolve
  scoped service '...' from root provider.` Loud, but only because Development happens to validate
  this; nothing stops the same misconfiguration in Production.
- With `ValidateScopes` off — the common Production default unless explicitly configured — the
  container silently hands back a **captive singleton**: the first request's `HttpChangeContextProvider`
  instance, with whatever it captured from `IHttpContextAccessor` at construction, reused for every
  later `SaveChanges` regardless of which request is actually running. Every history row after the
  first is silently stamped with the wrong user.

There is no registration pattern for the provider itself that avoids this — the fixed point is
`ApplicationServiceProvider`, not the provider's own lifetime. Hindsight resolves it fresh from the
current `DbContext` instance on every `SaveChanges`, but every pooled/factory-created instance points
at the same pinned, captured provider regardless. The fix is to register the provider `Singleton` and
read per-request state fresh inside `GetChangeContext`, rather than capturing a scoped dependency in
its constructor. `IHttpContextAccessor` is itself already a singleton, backed by `AsyncLocal`, so the
sample provider above needs no change beyond its registration:

```csharp
services.AddHttpContextAccessor();
services.AddSingleton<HttpChangeContextProvider>(); // not AddScoped

services.AddDbContextPool<AppDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseHindsight(h => h.WithChangeContext<HttpChangeContextProvider>()));
```

`HttpChangeContextProvider` above already only reads `accessor.HttpContext` inside `GetChangeContext`
— nothing is captured at construction — so making it a singleton is enough; no other code changes.
The same applies to `AddDbContextFactory<T>()` and `AddPooledDbContextFactory<T>()`.

This is a hard constraint, not something Hindsight can detect and correct at runtime: from inside the
resolution call there is no public API that distinguishes "a captured root provider silently handing
back a captive singleton" from "a correctly scoped provider that happens to already exist" — both look
identical to `IServiceProvider.GetService`. `HindsightOptionsExtension.Validate` cannot help either: it
runs before any service provider necessarily exists to inspect. The only case Hindsight can improve is
the loud one — `ValidateScopes` on — where the thrown `InvalidOperationException` names the provider
and this section instead of forwarding ASP.NET Core's generic, Hindsight-unaware message on its own.

### Trust model

The change-context columns are **not tamper-resistant against anything that can open its own
connection to the database.**

Under `HistoryWriter.Interceptor`, the values come from your `IChangeContextProvider` and are bound
as ordinary parameters on the history `INSERT` your application issues — this path is only as
trustworthy as your application code itself.

Under `HistoryWriter.Trigger`, Hindsight pushes the same values into the session with
`set_config('hindsight.changed_by', ..., true)` (and one `set_config` per column) once per
`SaveChanges`, and the trigger function reads them back with `current_setting('hindsight.changed_by',
true)` when it writes a history row — see [History writers → Change
context](history-writers.md#change-context). `set_config` with `is_local = true` sets a value for the
rest of the *current transaction*, nothing more: it is not authenticated, not tied to a role or login,
and not scoped to Hindsight's own code path in any way. **Any session with an ordinary (non-superuser)
connection to the database can run:**

```sql
SELECT set_config('hindsight.changed_by', 'someone-else', true);
UPDATE policies SET status = 'Active' WHERE id = 1;
```

and the resulting history row will show `changed_by = 'someone-else'`, indistinguishable from a value
the application itself pushed. The same applies to `changed_by_name`, `correlation_id`, `reason` and
`extra`. Nothing here is bound to anything PostgreSQL itself vouches for — `session_user`,
`current_user`, `inet_client_addr()`, the role a connection actually authenticated as — because
Hindsight does not currently capture any of those (see `DESIGN.md` for an open design question about
adding one as a separate, additional column).

This is a deliberate trade-off (DESIGN.md D3), not an oversight: Hindsight assumes

- the application's own database credentials are not shared with untrusted parties, and
- raw `psql` (or any other direct-connection tool) access to the production database is itself
  controlled and audited by means outside Hindsight — the same assumption almost every application
  already makes about its production database.

If your threat model includes people or processes with a legitimate, ordinary database connection who
should *not* be able to forge `changed_by` on a history row, treat the change-context columns as
**application-asserted, not database-guaranteed** — the same trust level as, say, a `created_by`
column an application sets on an ordinary table. Do not present them as a non-repudiation mechanism
without an additional, independent control (e.g. row-level security limiting who can run
`set_config('hindsight.*', ...)`, or an external audit log of raw database access).

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
- the period-start and period-end column names are the same;
- the history table name, or any name Hindsight derives from it (the trigger function and trigger in
  `HistoryWriter.Trigger` mode, or either of the two indexes), would exceed 63 bytes — PostgreSQL's
  identifier limit (`NAMEDATALEN - 1`; it counts UTF-8 bytes, not characters). PostgreSQL truncates a
  longer identifier to 63 bytes silently instead of erroring, so two entities whose names differ only
  after that point can end up sharing the same physical table, index, function or trigger — a
  `CREATE TABLE`/`CREATE INDEX` collision fails loudly when the migration is applied, but `CREATE OR
  REPLACE FUNCTION` does not: it silently replaces one entity's trigger function body with the
  other's. Give the entity a shorter history table name with `UseHistoryTable("shorter_name")`, or
  rename its main table.

A history table name that collides with another table in the model is also rejected, but by EF
Core's own model validation rather than a Hindsight-specific message, since the history entity type
is an ordinary property-bag entity type as far as EF Core is concerned.
