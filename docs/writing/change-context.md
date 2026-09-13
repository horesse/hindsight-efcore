---
order: 40
---

# Change context

The database knows that a row changed. Your application knows who was acting, on behalf of which
request, and why. Hindsight stores that *change context* on every version, in five columns:

| `ChangeContext` member | history column | type |
|---|---|---|
| `UserId` | `changed_by` | `text` |
| `UserName` | `changed_by_name` | `text` |
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
not be able to forge `changed_by`, add an independent control, such as row-level security or an
external audit of direct database access. Recording PostgreSQL's own `session_user` as an extra column
is an open question in the [design decisions](/reference/design) (D16).
