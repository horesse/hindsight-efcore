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
| with `EnableRetryOnFailure()` and no transaction | trigger writer: lets EF Core open one per attempt; interceptor writer: throws `InvalidOperationException`. See below |

¹ The trigger writer only needs its own transaction to push a [change context](/writing/change-context).
Without a provider or `WithReason`, it opens none: the trigger writes history inside the same
statement anyway.

## Your own transaction

Begin a transaction before `SaveChanges` and Hindsight uses it:

<<< @/snippets/Transactions.cs#own-transaction

## `EnableRetryOnFailure` {#enableretryonfailure}

A retrying execution strategy can re-run a whole `SaveChanges` after a transient failure. A transaction
opened *inside* that call would not survive the retry, so EF Core refuses it. Aspire turns the strategy
on by default.

The [trigger writer](/writing/history-writers) needs no transaction of its own here. It asks EF Core to
open one inside the execution strategy, and pushes the [change context](/writing/change-context) into
it. A retried attempt runs in a new transaction and gets the context again, so a plain `SaveChanges`
just works:

<<< @/snippets/Transactions.cs#retry-options

The interceptor writer writes history rows after EF Core's own batch, outside the execution strategy,
so a retry could not repeat them. A save that would make it open its own transaction throws
`InvalidOperationException` instead, naming the conflict and the fix. The fix, which also works under
the trigger writer, is the pattern EF Core recommends for any transaction under retry: run the unit of
work through the execution strategy and open the transaction yourself.

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
