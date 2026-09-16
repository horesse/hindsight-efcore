---
order: 10
---

# Temporal entities

An entity becomes temporal with one call in `OnModelCreating`:

<<< @/snippets/AppDbContext.cs#mark-temporal

The next `dotnet ef migrations add` creates its history table. See
[What a migration creates](/migrations/generated-schema).

## Defaults

| setting | default |
|---|---|
| history table | `<main table>_history`, in the main table's schema |
| period columns | `valid_from`, `valid_to` |
| versioned properties | every mapped property of the entity |

## Customizing

Pass a builder to `IsTemporal` to change any default:

<<< @/snippets/Configuration.cs#configure-temporal

| method | effect |
|---|---|
| `UseHistoryTable(name, schema)` | Names the history table, and optionally its schema. Changing it later renames the table; see [Renaming tables](/migrations/renaming-tables). |
| `HasPeriodStart(column)`, `HasPeriodEnd(column)` | Names the period columns. |
| `Exclude(property)` | Leaves a property out of the history; see below. |
| `WithDbSessionUser()` | Adds the `db_session_user` audit column; see [Change context](/writing/change-context#database-session-user). |

## Excluding properties

`Exclude` removes a property from versioning completely:

- the history table has no column for it;
- a `SaveChanges` that changed **only** excluded properties writes no history row.

Use it for bookkeeping columns that change often and mean little on their own, such as `UpdatedAt` or
a cached calculation. The sample project excludes `Policy.UpdatedAt`:

<<< @/../samples/InsuranceSample/InsuranceDbContext.cs

You can exclude some properties of a composite primary key, but not all of them: Hindsight needs at
least one key column to tell which history rows belong to which entity.

## What can be temporal

A temporal entity must be a standalone entity type mapped to its own table, with a primary key. Owned
references, complex properties and inheritance hierarchies are rejected when the model is built. See
[Model validation](/configuration/model-validation) for the full list and what to do instead.
