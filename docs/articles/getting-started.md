# Getting started

> [!WARNING]
> Hindsight is pre-alpha. This guide describes the target v1 experience; parts of it are not implemented yet.
> Track progress in the [changelog](https://github.com/horesse/hindsight-efcore/blob/master/CHANGELOG.md).

## Requirements

- .NET 10
- EF Core 10 with `Npgsql.EntityFrameworkCore.PostgreSQL`
- PostgreSQL 14 or later, a regular database user with rights on its own schema

## Install

```
dotnet add package Hindsight.EntityFrameworkCore.PostgreSQL
```

## Mark an entity as temporal

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Policy>().IsTemporal();
}
```

## Enable Hindsight on the context

```csharp
services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseHindsight());
```

## Add a migration

```
dotnet ef migrations add MakePoliciesTemporal
```

The migration creates `policies_history` next to `policies`, with the same columns plus the period
and change-context columns, and the indexes `AsOf()` needs. See [Schema evolution](schema-evolution.md)
for what happens when `Policy` changes later.

## Query

```csharp
var atClaim = await db.Policies.AsOf(claim.OccurredAt).SingleAsync(p => p.Id == id);
```

Continue with [Concepts](concepts.md).
