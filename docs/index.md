---
_layout: landing
---

# Hindsight

**System-versioned temporal entities for EF Core on PostgreSQL.**

No database extensions. No superuser. History tables live in your EF Core migrations, the change
context (who, why, correlation id) comes from your application, and `AsOf()` is plain LINQ.

```csharp
modelBuilder.Entity<Policy>().IsTemporal();

var asOfClaim = await db.Policies.AsOf(claim.OccurredAt).SingleAsync(p => p.Id == id);
```

- [Getting started](articles/getting-started.md)
- [Concepts: system time vs. application time](articles/concepts.md)
- [Configuration](articles/configuration.md)
- [Querying history](articles/querying.md)
- [History writers: Interceptor vs. Trigger](articles/history-writers.md)
- [Schema evolution](articles/schema-evolution.md)
- [API reference](api/index.md)
