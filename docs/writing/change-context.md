---
order: 40
---

# Change context

The database knows that a row changed. Your application knows who was acting, on behalf of which
request, and why. Hindsight stores that *change context* on every version, in five columns:

| `ChangeContext` member | history column | type |
|---|---|---|
| `ChangedBy` | `changed_by` | `text` |
| `ChangedByName` | `changed_by_name` | `text` |
| `CorrelationId` | `correlation_id` | `text` |
| `Reason` | `reason` | `text` |
| `Extra` | `extra` | `jsonb` |

They are `NULL` until you register a provider.

## Write a provider

Implement `IChangeContextProvider`. This one reads the signed-in user and the current trace id in an
ASP.NET Core application:

<<< @/snippets/ChangeContext.cs#http-provider

- A `null` member leaves its column `NULL`.
- `Extra` is written into the `jsonb` column as is, so it must be valid JSON; serialize it yourself.
- `GetChangeContext` is synchronous: it runs inside `SaveChangesAsync` too, and blocking on an async
  source there would be sync-over-async. Read ambient state; don't call a database or a web service.

## Register it

<<< @/snippets/ChangeContext.cs#register-provider

- The provider is called **once per `SaveChanges`** that writes a temporal entity, never once per row.
- It is resolved from the application's service provider, so it can take dependencies. A type with a
  parameterless constructor is created directly.
- If it throws, `SaveChanges` fails and the whole transaction, including the change, rolls back.
- Hindsight ships no default provider; mapping claims to a user id is your application's decision.

Register the provider as a **singleton** and read per-request state inside `GetChangeContext`, as
above. That works with every kind of context registration; a scoped provider does not (see below).

## A reason for one operation

`WithReason` sets the reason for the saves inside a `using` block, overriding
`ChangeContext.Reason`:

<<< @/snippets/ChangeContext.cs#with-reason

- It works with or without a provider.
- Scopes nest; the innermost one wins.
- It applies to that `DbContext` instance only. A save on another context inside the same block is not
  affected.

## Pooled and factory-created contexts

::: danger Never register the provider as scoped with pooling or a context factory
With `AddDbContextPool`, `AddDbContextFactory` or `AddPooledDbContextFactory`, every context shares
options built once, from the application's root service provider. A scoped provider is then resolved
from the root:

- with scope validation on (ASP.NET Core's Development default), every save throws;
- with it off (the usual Production setting), you silently get one captive instance, and **every
  version is stamped with whatever the first request captured**.
:::

Register a singleton that reads per-request state when it is called. `IHttpContextAccessor` is itself a
singleton backed by `AsyncLocal`, so the provider above needs nothing else:

<<< @/snippets/ChangeContext.cs#register-pooled

Hindsight cannot detect the silent case at runtime; a captive instance and a correctly scoped one look
the same from inside the container. It can only improve the loud case: the exception names the
provider and points here.

## Writes that bypass `SaveChanges`

`ExecuteUpdate`, `ExecuteDelete` and raw SQL never call the provider. Under the trigger writer they are
recorded with `NULL` change-context columns; under the interceptor writer they are not recorded at all.

## Trust model

The change-context columns are **asserted by the application, not guaranteed by the database**. Treat
them like a `created_by` column that your application sets on an ordinary table.

- Under the interceptor writer, the values are parameters of an `INSERT` your application sends. They
  are as trustworthy as your application code.
- Under the trigger writer, they travel through `set_config('hindsight.*', …, true)`: an ordinary,
  unauthenticated session setting. Anyone with a normal connection to the database can run

  ```sql
  SELECT set_config('hindsight.changed_by', 'someone-else', true);
  UPDATE policies SET status = 'Active' WHERE id = 1;
  ```

  and the history row will show `changed_by = 'someone-else'`, indistinguishable from one your
  application wrote.

This is a deliberate trade-off: Hindsight assumes the application's database credentials are not
shared with untrusted parties, and that direct access to the production database is controlled and
audited by other means. If your threat model includes people with legitimate database access who must
not be able to forge `changed_by`, add an independent control, such as row-level security, an external
audit of direct database access, or the `db_session_user` column below.

## Database session user

`WithDbSessionUser()` adds a second, defense-in-depth column PostgreSQL itself guarantees instead of
the application asserting:

<<< @/snippets/Configuration.cs#with-db-session-user

| column | type | populated by |
|---|---|---|
| `db_session_user` | `text not null default session_user` | PostgreSQL's own `session_user`: the role that authenticated the connection which executed the write |

Unlike `changed_by` and the rest of the change context, `db_session_user` **cannot be forged** with
`set_config` — it is filled in by PostgreSQL's own `DEFAULT session_user`, immune to anything the
application or an attacker sends over the connection, in both writer modes and for
`ExecuteUpdate`/`ExecuteDelete`/raw SQL under the trigger writer alike.

- It answers a different question than `changed_by`: *which database role actually executed this
  write*, not *which end user*. A pooled application connection's `session_user` is typically one
  shared service role, so in practice `db_session_user` mostly confirms "yes, this came in through the
  app's own role" — its value is in the negative case, catching a write that did *not*.
- Only `session_user` is captured, never `current_user`. They differ under `SET ROLE` /
  `SECURITY DEFINER` — `session_user` is who authenticated and cannot change without re-authenticating;
  `current_user` can change mid-session, which would let it be spoofed the same way `changed_by` can.
- It is **opt-in** (`IsTemporal(t => t.WithDbSessionUser())`), not part of the base history columns,
  because it is not universally useful — see the pooled-connection point above — and because it is the
  one history column shaped around PostgreSQL's own authentication rather than the application's.
- It does not defend against someone who genuinely holds the application's own database credentials and
  uses them from `psql` instead of through the app: `session_user` is unchanged either way. It only
  catches a write that came in under a *different* role entirely.
- On `Version<T>.DbSessionUser` it reads back `null` when the entity did not opt in — the column does
  not exist on that entity's history table at all — never `null` for an opted-in entity's own stored
  rows, since the database's `not null` constraint guarantees a value on every one of them.

See [design decisions](/reference/design) (D16) for the full trust-model write-up and the mechanism
that makes this "free": the column is populated by PostgreSQL's own `DEFAULT`, not by anything
`HistoryRowWriter` or the trigger function explicitly writes.
