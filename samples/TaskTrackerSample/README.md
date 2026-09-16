# TaskTrackerSample

A runnable tour of `HistoryWriter.Trigger` and change context: who changed a row, and why - plus what
happens to a write that never goes through `SaveChanges` at all.

```
dotnet run --project samples/TaskTrackerSample
```

Needs Docker. It starts and tears down its own disposable PostgreSQL 17 container — nothing to
configure, nothing left behind.

What it does, in order: two simulated users (`alice`, `bob`) create and move a work item through its
statuses, each change tagged with who made it and why (`DbContext.WithReason`); `History<WorkItem>()`
prints that audit trail. Then it runs a plain `ExecuteUpdateAsync` — no `SaveChanges`, no interceptor in
the loop at all — and shows the trigger still recorded it, this time with no change context to attach.

See [`Program.cs`](Program.cs) for the whole thing, or the
[change context](https://horesse.github.io/hindsight-efcore/latest/writing/change-context) and
[history writers](https://horesse.github.io/hindsight-efcore/latest/writing/history-writers) docs for
how each piece works.
