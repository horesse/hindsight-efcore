---
order: 10
sidebarTitle: An audit trail for insurance policies
---

# Tutorial: an audit trail for insurance policies

An insurer has to answer questions like these, sometimes years later: *what exactly did this policy
say when the claim was filed? Who raised the premium, and why?* In this tutorial you give an ASP.NET
Core API those answers by adding Hindsight to it.

You will:

1. make the `Policy` entity temporal;
2. record who made each change and why;
3. generate the migration and look at what it creates;
4. write through the API as usual;
5. ask point-in-time and audit questions.

It takes about 15 minutes. You need an ASP.NET Core minimal API project with EF Core, and a PostgreSQL
database you can migrate. A throwaway one is enough:

```bash
docker run -d --name insurance-db -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17
```

## 1. Add the packages

```bash
dotnet add package Hindsight.EntityFrameworkCore.PostgreSQL
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet tool install --global dotnet-ef
```

## 2. The entity

A policy has a number, a status, a premium and a start date:

<<< @/../samples/InsuranceSample/Policy.cs

`UpdatedAt` is bookkeeping. It changes often and says nothing about the policy itself, so it will be
kept out of the history.

## 3. Make it temporal

<<< @/../samples/InsuranceSample/InsuranceDbContext.cs

`IsTemporal` gives `Policy` a history table. `Exclude(p => p.UpdatedAt)` leaves that column out of
the history, and a save that changes only `UpdatedAt` creates no new version.

## 4. Record who and why

Hindsight stores a *change context* with every version: the user, a correlation id and a reason. It
gets them from an `IChangeContextProvider`, which you write. This one reads the signed-in user and the
current trace id:

<<< @/snippets/ChangeContext.cs#http-provider

Register it together with the context:

<<< @/snippets/Tutorial.cs#tutorial-register

Two choices here:

- **`HistoryWriter.Trigger`**: history is written by a PostgreSQL trigger, so even a raw SQL `UPDATE`
  run by hand gets recorded. See [Choosing a history writer](/writing/history-writers).
- **A singleton provider**: it reads the request's user when it is called, so one instance serves
  every request. See [Change context](/writing/change-context#pooled-and-factory-created-contexts)
  for why it should not be scoped.

## 5. Generate the migration

```bash
dotnet ef migrations add AddPolicyHistory
dotnet ef database update
```

The migration's C# creates `policies` and `policies_history`. Hindsight adds two more things when the
migration is turned into SQL: a GiST index on the history table's period, and the trigger. Print the
full SQL to review it:

```bash
dotnet ef migrations script
```

::: details The generated trigger, for a `policies` table
This is the exact output from Hindsight's own test suite, for a policy table with `id`, `number`,
`premium` and `status` columns:

<<< @/../tests/Hindsight.IntegrationTests/TriggerDdlTests.Trigger_ddl_has_the_expected_shape.verified.sql
:::

## 6. Write through the API

The endpoints look like any other EF Core code. The only addition is `WithReason`, which attaches a
reason to the next save:

<<< @/snippets/Tutorial.cs#tutorial-write

Create a policy, activate it and raise its premium:

```bash
API=http://localhost:5000

curl -X POST $API/policies -H 'Content-Type: application/json' \
     -d '{"number":"ACME-1001","premium":1200,"effectiveFrom":"2026-10-01"}'
curl -X POST "$API/policies/1/activate?reason=Signed%20by%20the%20customer"
curl -X PUT  "$API/policies/1/premium?premium=1350&reason=Risk%20re-assessment"
```

`policies` holds one row, as before. `policies_history` now holds three versions of it:

| `operation` | `status` | `premium` | `valid_from` | `valid_to` | `reason` |
|---|---|---|---|---|---|
| 1 (insert) | `Draft` | 1200 | 10:00:00 | 10:01:00 | |
| 2 (update) | `Active` | 1200 | 10:01:00 | 10:02:00 | Signed by the customer |
| 2 (update) | `Active` | 1350 | 10:02:00 | `infinity` | Risk re-assessment |

## 7. Ask the questions

### What did the policy say at a given moment?

<<< @/snippets/Tutorial.cs#tutorial-as-of

`AsOf` reads the version that was current at that instant. A claim filed at 10:01:30 sees the active
policy at the old premium of 1200; a policy that did not exist yet, or was already deleted, returns
404.

### Who changed it, and why?

<<< @/snippets/Tutorial.cs#tutorial-history

`History<Policy>()` returns every version with its period and change context, newest first:

```json
[
  { "validFrom": "2026-09-14T10:02:00Z", "operation": 2, "changedByName": "alice", "reason": "Risk re-assessment", "status": 1, "premium": 1350 },
  { "validFrom": "2026-09-14T10:01:00Z", "operation": 2, "changedByName": "alice", "reason": "Signed by the customer", "status": 1, "premium": 1200 },
  { "validFrom": "2026-09-14T10:00:00Z", "operation": 1, "changedByName": "alice", "reason": null, "status": 0, "premium": 1200 }
]
```

### What was deleted, and by whom?

After a `DELETE /policies/1?reason=Duplicate`, the policy is gone from `policies`, and `AsOf` stops
finding it from that instant on. Its history stays, ending in a *tombstone* that records the delete:

<<< @/snippets/Tutorial.cs#tutorial-deleted

## What you built

- Every change to a policy is kept, with who made it and why, in the same transaction as the change.
- Point-in-time and audit questions are ordinary LINQ queries.
- The history schema is in your migrations and evolves with the entity. Removing a property later
  keeps its history column; see [Evolving a temporal entity](/migrations/schema-evolution).

## Next steps

- [Rules for historical queries](/querying/restrictions): what composes with `AsOf`, and what throws.
- [Trust model](/writing/change-context#trust-model): what the change-context columns do and do not
  prove.
- [Transactions](/writing/transactions): using Hindsight with your own transactions, retries and
  `TransactionScope`.
