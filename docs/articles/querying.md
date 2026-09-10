# Querying history

You have a `policies` table and a `policies_history` table that Hindsight keeps in step on every
`SaveChanges`. This page is about reading the history back.

## Point in time: `AsOf`

"What did this policy look like when the claim was filed?"

```csharp
var policy = await db.Policies
    .AsOf(claim.OccurredAt)
    .SingleAsync(p => p.Id == claimPolicyId);
```

`AsOf(instant)` switches the query from the `policies` table to `policies_history` and keeps only the
version whose system-time period contains `instant` — the predicate is
`valid_from <= @instant AND valid_to > @instant`, a half-open interval, so the row is the one that was
live in the database at that moment. A policy that had not been created yet at `instant`, or that had
already been deleted, returns nothing.

Everything after `AsOf` composes as usual and goes to the database as **one** SQL query:

```csharp
var richDraftNumbers = await db.Policies
    .AsOf(endOfLastQuarter)
    .Where(p => p.Status == PolicyStatus.Draft && p.Premium > 10_000m)
    .OrderByDescending(p => p.Premium)
    .Select(p => p.Number)
    .ToListAsync();
```

`AsOf` must be the **first** operator on the query, applied straight to the `DbSet`. The instant is
sent as a query parameter, so different instants share one compiled query and one query-plan cache
entry.

## Every version: `AllVersions`

"Show me this policy's whole history."

```csharp
var timeline = await db.Policies
    .AllVersions()
    .Where(p => p.Id == id)
    .ToListAsync();
```

`AllVersions()` switches the query from `policies` to `policies_history` and returns **every stored
version** — one row per `insert` and per `update`. There is no period predicate: this is the full
timeline, not a point in time. Rows come back **newest first**, ordered by `valid_from` descending;
add your own `OrderBy` / `OrderByDescending` to replace that ordering (a trailing `ThenBy` keeps
newest-first as the primary key). Like `AsOf`, it must be the **first** operator on the query and
everything after it composes into one SQL statement:

```csharp
var premiumChanges = await db.Policies
    .AllVersions()
    .Where(p => p.Id == id)
    .Select(p => p.Premium)
    .Distinct()
    .ToListAsync();
```

The `delete` tombstone is **not** a version — a deleted policy's timeline ends at the version that
was open when it was deleted, and `AllVersions()` never returns a row for the deletion itself. The
*when* and *who* of a delete live in the change-context columns; read them with `History<Policy>()`
(below).

## Version metadata: `History<T>`

"Who changed this policy, when, and why?"

`AllVersions()` gives you the column values of every version. `History<Policy>()` gives you each
version wrapped in a <xref:Hindsight.Version`1> — the entity snapshot **plus** the version's
system-time period and the change-context columns:

```csharp
var audit = await db.History<Policy>()
    .Where(v => v.Entity.Id == id)
    .Select(v => new { v.ValidFrom, v.ValidTo, v.Operation, v.ChangedBy, v.Reason, v.Entity.Status })
    .ToListAsync();
```

It is an extension on `DbContext` (not on a `DbSet`), so you name the entity type:
`db.History<Policy>()`. Each `Version<Policy>` carries:

| member | source |
|---|---|
| `Entity` | the `Policy` snapshot for that version, reconstructed from the versioned columns |
| `ValidFrom` / `ValidTo` | the half-open system-time period, `DateTimeOffset` in UTC |
| `IsCurrent` | `true` when `ValidTo` is `DateTimeOffset.MaxValue` — the version live right now |
| `Operation` | `VersionOperation.Insert` / `Update` / `Delete` |
| `ChangedBy`, `ChangedByName`, `CorrelationId`, `Reason`, `Extra` | the change-context columns, or `null` when no `IChangeContextProvider` supplied them |

Unlike `AllVersions()`, `History<T>()` **includes the `delete` tombstone** (DESIGN.md D5): a deleted
row comes back as a `Version` with `Operation == VersionOperation.Delete` and an empty interval
(`ValidFrom == ValidTo`), carrying the last column values and the change context of the deletion —
this is the delete audit. Rows are newest first (by `valid_from` descending, then insertion order);
your own `OrderBy` / `OrderByDescending` replaces that.

Filtering and projection compose into one SQL query through both the metadata members and the entity
snapshot — `Where(v => v.Entity.Id == id)` and `Select(v => new { v.ValidFrom, v.Entity.Status })`
translate, no client evaluation:

```csharp
var deletions = await db.History<Policy>()
    .Where(v => v.Operation == VersionOperation.Delete)
    .Select(v => new { v.Entity.Number, v.ValidFrom, v.ChangedBy, v.Reason })
    .ToListAsync();
```

## Rules

These apply to `AsOf`, `AllVersions` and `History<T>`.

- **Always no-tracking.** Results are detached snapshots — the change tracker is untouched.
  `AsOf(...).AsTracking()` / `AllVersions().AsTracking()` / `History<Policy>().AsTracking()` throws: a
  historical row is not something you edit and save back.
- **Read-only — enforced.** Re-attaching a snapshot (`db.Update(snapshot)`, `db.Attach(...)`,
  `db.Remove(...)`, `db.Entry(...).State = ...`) and calling `SaveChanges` throws
  `InvalidOperationException`: it would write a past snapshot back as the current version and generate
  spurious history. This holds for the entity from `AsOf` / `AllVersions` and for `Version<T>.Entity`
  from `History<T>`. To roll a value back, copy it onto a fresh instance — or onto one from
  `DbSet.Find()` / a normal query — and save that.
- **No `Include`.** `AsOf(...).Include(...)` / `AllVersions().Include(...)` /
  `History<Policy>().Include(...)` throws `NotSupportedException`. Reading a related entity from
  history is an interval join on two periods; v1 refuses rather than return a result that looks right
  and is not (see [DESIGN.md](../design.md) D8). Load the related rows with a second history query
  keyed on the foreign key you already have.
- **`AsOf` / `AllVersions` go first.** `db.Policies.Where(...).AsOf(t)` throws — put them directly on
  the `DbSet`, before `Where` / `OrderBy` / `Select`. (`History<T>()` starts from the `DbContext`, so
  there is nothing to put it after.)
- **Standalone entities only.** An entity in an inheritance hierarchy, or one with owned or complex
  members, throws `NotSupportedException` — those shapes are not carried on the history table in v1.
  Read `policies_history` directly with `FromSql` for them.
- **Temporal only.** `AsOf` / `AllVersions` / `History<T>()` on an entity that was never
  `IsTemporal()` throws `InvalidOperationException` naming the entity.
