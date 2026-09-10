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

> [!WARNING]
> `History<T>()` is not implemented yet. The snippet below is the intended shape; today `AsOf` and
> `AllVersions` are the way to read history.

```csharp
// versions plus the "who / why" columns
var audit = await db.History<Policy>()
    .Where(v => v.Entity.Id == id)
    .OrderByDescending(v => v.ValidFrom)
    .Select(v => new { v.ValidFrom, v.ValidTo, v.Operation, v.ChangedBy, v.Reason, v.Entity.Status })
    .ToListAsync();
```

## Rules

These apply to both `AsOf` and `AllVersions`.

- **Always no-tracking.** Results are detached snapshots — the change tracker is untouched.
  `AsOf(...).AsTracking()` / `AllVersions().AsTracking()` throws: a historical row is not something
  you edit and save back.
- **No `Include`.** `AsOf(...).Include(...)` / `AllVersions().Include(...)` throws
  `NotSupportedException`. Reading a related entity from history is an interval join on two periods;
  v1 refuses rather than return a result that looks right and is not (see
  [DESIGN.md](../design.md) D8). Load the related rows with a second history query keyed on the
  foreign key you already have.
- **The history operator goes first.** `db.Policies.Where(...).AsOf(t)` throws — put `AsOf` /
  `AllVersions` directly on the `DbSet`, before `Where` / `OrderBy` / `Select`.
- **Standalone entities only.** An entity in an inheritance hierarchy, or one with owned or complex
  members, throws `NotSupportedException` — those shapes are not carried on the history table in v1.
  Read `policies_history` directly with `FromSql` for them.
- **Temporal only.** `AsOf` / `AllVersions` on an entity that was never `IsTemporal()` throws
  `InvalidOperationException` naming the entity.
