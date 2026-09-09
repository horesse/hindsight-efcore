# Querying history

## Point in time

```csharp
var policy = await db.Policies
    .AsOf(claim.OccurredAt)
    .SingleAsync(p => p.Id == id);
```

`AsOf` switches the query source to the history table with the predicate
`valid_from <= @t AND valid_to > @t`. Everything after it — `Where`, `OrderBy`, `Select`, `First` —
composes as usual.

## All versions

```csharp
var versions = await db.Policies
    .AllVersions()
    .Where(p => p.Id == id)
    .ToListAsync();          // newest first
```

## Versions with metadata

```csharp
var audit = await db.History<Policy>()
    .Where(v => v.Entity.Id == id)
    .OrderByDescending(v => v.ValidFrom)
    .Select(v => new
    {
        v.ValidFrom,
        v.ValidTo,
        v.Operation,
        v.ChangedBy,
        v.CorrelationId,
        v.Reason,
        v.Entity.Status,
    })
    .ToListAsync();
```

`History<T>()` returns `IQueryable<Version<T>>`, where `Version<T>` exposes both the entity snapshot
and the version metadata.

## Rules

- Historical queries are **always no-tracking**. Calling `Update` or `Remove` on an entity that came
  from `AsOf`, `AllVersions` or `History<T>` throws.
- `AsOf(...).Include(...)` throws `NotSupportedException` in v1. Joining related entities at a point in
  time is an interval join; a silently wrong result would be worse than the exception.
