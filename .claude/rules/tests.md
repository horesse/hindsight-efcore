---
paths:
  - "tests/**/*.cs"
---

# Rules for tests

- Unit tests (`Hindsight.Tests`) build a model and inspect metadata. They never open a connection;
  `UseNpgsql("Host=localhost;Database=unused")` is fine because nothing connects.
- Integration tests (`Hindsight.IntegrationTests`) use `PostgresFixture`: one container per collection,
  one database per test via `CreateDatabaseAsync(nameof(TheTest))`. Never share a database between tests.
- Every scenario that writes history runs in both writer modes: use a `[Theory]` with
  `[InlineData(HistoryWriter.Interceptor)]` / `[InlineData(HistoryWriter.Trigger)]` once both exist.
- Assert on *intervals*, not on timestamps: contiguous (`prev.ValidTo == next.ValidFrom`), no gaps,
  no overlaps, exactly one row with `ValidTo == infinity` per live entity, zero for deleted ones.
- When asserting generated SQL or DDL, use Verify snapshots (`*.verified.sql`). Do not assert on
  substrings of SQL.
- A red test is information. Do not fix it by loosening the assertion, adding `Skip`, or switching
  the provider. If the behavior under test is actually wrong per `DESIGN.md`, say so and stop.
- Test names describe behavior: `Update_touching_only_excluded_property_writes_no_history_row`.
- No `Thread.Sleep` / `Task.Delay` to "let time pass" — inject `TimeProvider` (Interceptor) or use
  distinct transactions (Trigger).
