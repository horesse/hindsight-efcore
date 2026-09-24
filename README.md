# Hindsight

**System-versioned temporal entities for EF Core 10 on PostgreSQL.**
No database extensions, no superuser, history lives in your migrations, and the change context
(who / why / correlation id) comes from the application.

[![CI](https://github.com/horesse/hindsight-efcore/actions/workflows/ci.yml/badge.svg)](https://github.com/horesse/hindsight-efcore/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Hindsight.EntityFrameworkCore.PostgreSQL.svg)](https://www.nuget.org/packages/Hindsight.EntityFrameworkCore.PostgreSQL)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-horesse.github.io-0f766e.svg)](https://horesse.github.io/hindsight-efcore/)

**Documentation: <https://horesse.github.io/hindsight-efcore/>**, versioned per release, with a
[getting started](https://horesse.github.io/hindsight-efcore/latest/introduction/getting-started) guide
and a [tutorial](https://horesse.github.io/hindsight-efcore/latest/tutorials/audit-trail).

## What is Hindsight

Every insert, update and delete on a Hindsight entity keeps the previous state as a *version* in a
history table, next to the main one, with the exact period during which that version was current.
The main table is untouched — same columns, indexes, foreign keys, queries. You read history back
with typed LINQ: `AsOf(instant)` for a point in time, `History<T>()` for who changed what and why.

| | Hindsight | EF Core temporal tables | `temporal_tables` extension | Audit.NET |
|---|---|---|---|---|
| PostgreSQL | ✅ | ❌ SQL Server only | ✅ | ✅ |
| Needs superuser / extension install | ❌ plain user, PG 14+ | — | ✅ C extension | ❌ |
| History schema in EF migrations | ✅ | ✅ | ❌ by hand | ❌ |
| Point-in-time queries as typed LINQ | ✅ | ✅ | ❌ raw SQL | ❌ |
| Who / why / correlation id | ✅ from the app¹ | ❌ | ❌ trigger sees rows only | ✅ |
| Half-open `[from, to)` intervals, `infinity` for current | ✅ | ✅ | ✅ | — |

¹ application-asserted, not database-guaranteed — see [trust
model](https://horesse.github.io/hindsight-efcore/latest/writing/change-context#trust-model).

PostgreSQL 19 brings *application-time* periods (`FOR PORTION OF`, `WITHOUT OVERLAPS`). It does
**not** bring *system-time* versioning — "when did the database hold this row" — and that is what
Hindsight does. See [DESIGN.md](DESIGN.md) for the distinction and the decisions behind it.

## 30-second example

```csharp
// configure
modelBuilder.Entity<Policy>().IsTemporal();

// what did this policy look like when the claim happened?
var policy = await db.Policies.AsOf(claim.OccurredAt).SingleAsync(p => p.Id == id);

// who changed it, and why?
var audit = await db.History<Policy>()
    .Where(v => v.Entity.Id == id)
    .Select(v => new { v.ValidFrom, v.ChangedBy, v.CorrelationId, v.Reason, v.Entity.Status })
    .ToListAsync();
```

## Installation

```
dotnet add package Hindsight.EntityFrameworkCore.PostgreSQL
```

Requires .NET 10, EF Core 10 and PostgreSQL 14 or later — a regular database user, no superuser, no
extensions.

```csharp
protected override void OnModelCreating(ModelBuilder b)
{
    b.Entity<Policy>().IsTemporal();                          // policies_history, valid_from / valid_to

    b.Entity<Risk>().IsTemporal(t => t
        .UseHistoryTable("risks_history", schema: "audit")
        .HasPeriodStart("sys_from").HasPeriodEnd("sys_to")
        .Exclude(r => r.RecalculatedAt));                    // noisy field, don't version it
}
```

```csharp
services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseHindsight(h => h
        .WithChangeContext<MyChangeContextProvider>()
        .UseHistoryWriter(HistoryWriter.Trigger)));          // or HistoryWriter.Interceptor
```

> [!IMPORTANT]
> `UseHindsight()` alone defaults to `HistoryWriter.Interceptor`. Call
> `UseHistoryWriter(HistoryWriter.Trigger)` for production use unless you specifically cannot grant
> `CREATE FUNCTION` / `CREATE TRIGGER` privileges — see
> [Choosing a history writer](https://horesse.github.io/hindsight-efcore/latest/writing/history-writers).

## Queries

```csharp
await db.Policies.AsOf(at).Where(...).ToListAsync();        // state at a point in time
await db.Policies.AllVersions().Where(...).ToListAsync();   // every version, newest first
await db.Policies.FromTo(q3Start, q3End).ToListAsync();     // every version valid at some point in [from, to)
await db.History<Policy>().Where(v => ...).ToListAsync();   // versions with metadata, tombstones included
```

Historical queries are always no-tracking; treat the results as read-only snapshots. `AsOf`,
`AllVersions` and the [time-range operators](https://horesse.github.io/hindsight-efcore/latest/querying/time-ranges)
`FromTo` / `ContainedIn` must be the first call on the `DbSet` — see
[rules for historical queries](https://horesse.github.io/hindsight-efcore/latest/querying/restrictions),
including why `AsOf` combined with `Include` throws instead of guessing.

## Audit

Hindsight stores who changed a row, on behalf of which request, and why — five columns on every
version: `ChangedBy`, `ChangedByName`, `CorrelationId`, `Reason`, `Extra`. You supply them by
implementing `IChangeContextProvider`, called once per `SaveChanges`, registered with
`UseHindsight(h => h.WithChangeContext<T>())`. `db.WithReason("...")` overrides the reason for one
call. `History<Policy>()` reads them back, newest first, tombstones included for deletes.

This is an audit trail, not a tamper-proof log: the change context is asserted by the application, not
enforced by the database. See [Change context](https://horesse.github.io/hindsight-efcore/latest/writing/change-context)
for the full contract and the [trust model](https://horesse.github.io/hindsight-efcore/latest/writing/change-context#trust-model).

## History

Two writers fill the history table, same contract:

| | `HistoryWriter.Interceptor` | `HistoryWriter.Trigger` |
|---|---|---|
| Where history is written | `SaveChangesInterceptor` in the app | plpgsql trigger generated by the migration |
| Catches `ExecuteUpdate` / raw SQL / other writers | ❌ | ✅ |
| Timestamp | `TimeProvider`, one per `SaveChanges` | `now()`, one per transaction |
| Superuser needed | no | no — trigger functions need only table ownership |

**Trigger** is recommended for production. Interceptor exists for environments where you cannot
create trigger functions at all, and as a reference implementation. Full comparison, overhead
numbers and how to switch: [Choosing a history writer](https://horesse.github.io/hindsight-efcore/latest/writing/history-writers).

Adding a column to a temporal entity adds it to the history table in the same migration. **Removing a
column never removes it from history** — it becomes nullable and stays, because old versions still
hold data in it. See [Evolving a temporal entity](https://horesse.github.io/hindsight-efcore/latest/migrations/schema-evolution).

## Limitations

- **Bitemporal (system + application time) is not implemented.** The API leaves room to add
  application time later without breaking changes.
- **`AsOf()` combined with `Include()` throws.** It is an interval join; Hindsight refuses rather than
  returning a silently wrong result.
- **Restoring an entity to a previous version is not automatic** — history is read-only, you copy the
  values back yourself.
- **PostgreSQL only.** No other providers are planned.
- **Not tamper-proof.** The change context is application-asserted, not database-guaranteed.

The complete, current list — including what throws under `HistoryWriter.Interceptor` specifically —
is [Limitations](https://horesse.github.io/hindsight-efcore/latest/reference/limitations).

## Samples

Two runnable, no-setup demos - each starts its own disposable PostgreSQL container:
`dotnet run --project samples/ProductCatalogSample` (query API) and
`dotnet run --project samples/TaskTrackerSample` (Trigger mode, change context). See
[Samples](https://horesse.github.io/hindsight-efcore/latest/introduction/samples) for what each shows.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Design decisions and their reasons are in [DESIGN.md](DESIGN.md);
if you disagree with one, open an issue against that section rather than a PR that silently changes it.

## License

MIT.
