---
order: 50
---

# Transactions

A change and its history rows always commit or roll back together. How that happens depends on the
transaction `SaveChanges` runs in:

| `SaveChanges` runs… | what Hindsight does |
|---|---|
| with no transaction | opens one around the save and commits it¹ |
| inside a transaction you began | writes history in your transaction; you commit |
| inside an ambient `TransactionScope` | opens none; the scope commits or rolls back |
| with `EnableRetryOnFailure()` and no transaction | throws `InvalidOperationException`; see below |

¹ The trigger writer only needs its own transaction to push a [change context](/writing/change-context).
Without a provider or `WithReason`, it opens none: the trigger writes history inside the same
statement anyway.

## Your own transaction

Begin a transaction before `SaveChanges` and Hindsight uses it:

<<< @/snippets/Transactions.cs#own-transaction

## `EnableRetryOnFailure` {#enableretryonfailure}

A retrying execution strategy can re-run a whole `SaveChanges` after a transient failure. A transaction
opened *inside* that call would not survive the retry, so EF Core refuses it, and Hindsight fails
early with a clear message instead:

<<< @/snippets/Transactions.cs#retry-options

A save that makes Hindsight open its own transaction then throws `InvalidOperationException`, naming
the conflict and the fix. The fix is the pattern EF Core recommends for any transaction under retry:
run the unit of work through the execution strategy and open the transaction yourself.

<<< @/snippets/Transactions.cs#retry-execution-strategy

Your transaction is open before the save starts, so Hindsight uses it, and the change and its history
are retried together.

::: warning `TransactionScope` is not a workaround
A retrying execution strategy refuses to run inside an ambient `TransactionScope` at all, with or
without Hindsight: EF Core throws before either writer runs.
:::

## Ambient `TransactionScope` {#transactionscope}

Without `EnableRetryOnFailure()`, an ambient `System.Transactions.TransactionScope` works with both
writers:

<<< @/snippets/Transactions.cs#transaction-scope

Npgsql enlists the connection in the ambient transaction when it opens, and every command after that,
including the history writes, runs in it. Hindsight sees the ambient transaction and opens none of its
own. `scope.Complete()` commits the change and its history together; disposing the scope without it
rolls both back.
