---
order: 20
sidebarTitle: Performance
---

# Performance

What Hindsight adds to a `SaveChanges`, measured with BenchmarkDotNet
(`benchmarks/Hindsight.Benchmarks`). One PostgreSQL 17 container serves the whole run; every case
uses its own database and measures one `SaveChanges` per iteration, re-seeding between iterations.
`None` is plain EF Core without Hindsight, the baseline.

Measured on an AMD Ryzen 7 7800X3D, Windows 11, .NET SDK 10.0.400, PostgreSQL 17 in Docker. **Your
numbers will differ**: compare the columns with each other, not with your hardware. To re-run:

```bash
dotnet run -c Release --project benchmarks/Hindsight.Benchmarks -- --filter '*'
```

## Insert, update, delete

Mean time of one `SaveChanges` over N temporal entities:

| operation | rows | None | Interceptor | Trigger |
|---|--:|--:|--:|--:|
| insert | 1 | 1.0 ms | 3.4 ms | 1.8 ms |
| insert | 100 | 9.3 ms | 16 ms | 12 ms |
| insert | 1000 | 63 ms | 106 ms | 83 ms |
| update | 1 | 0.7 ms | 3.1 ms | 1.0 ms |
| update | 100 | 7.5 ms | 23 ms | 13 ms |
| delete | 1 | 0.7 ms | 3.0 ms | 1.0 ms |
| delete | 100 | 6.0 ms | 22 ms | 12 ms |

Managed allocations for the same calls:

| operation | rows | None | Interceptor | Trigger |
|---|--:|--:|--:|--:|
| insert | 100 | 855 KB | 1580 KB | 960 KB |
| insert | 1000 | 8166 KB | 14930 KB | 9213 KB |
| update | 100 | 499 KB | 1367 KB | 592 KB |
| delete | 100 | 373 KB | 1228 KB | 478 KB |

- **Trigger** stays within about 1.2–2× plain EF Core. The database writes history inside the same
  statement; the application pays only for the change-context push, if any, and the transaction around
  it.
- **Interceptor** sends all history statements of a `SaveChanges` in one batched round trip, split
  every 512 rows. What remains is executing them: about 1.8× the baseline for a 100-row insert, about
  3× for a 100-row update or delete (two statements per row), and about 1.7× at 1000 rows.
- Single-row figures sit close to the round-trip noise floor; read the 100- and 1000-row rows for the
  trend.

## Change context

The same 100-row update, with and without an `IChangeContextProvider` (a fixed provider with no I/O,
so this is the plumbing only):

| writer | without | with | difference |
|---|--:|--:|--:|
| Interceptor | 23.0 ms | 23.8 ms | within noise |
| Trigger | 12.7 ms | 15.7 ms | +3.0 ms, +24 KB |

For the trigger writer, the difference is the one `set_config` round trip per `SaveChanges`. For the
interceptor writer, the provider call and the five extra columns fold into the round trip it already
makes.

## Tracked but unchanged entities

A context that tracks many entities but changes one of them still pays for change detection over the
whole graph. `ChangeTrackerOverheadBenchmarks` measures one `SaveChanges` that changes one `Policy`
while `TrackedUnchangedCount` other policies sit unchanged in the same context. Wall-clock time at this
scale is mostly noise, so allocations are the signal:

| TrackedUnchangedCount | None | Interceptor | Trigger |
|--:|--:|--:|--:|
| 0 | 9.09 KB | 26.51 KB | 10.61 KB |
| 100 | 59.88 KB | 134.55 KB | 115.53 KB |
| 1,000 | 516.75 KB | 1,102.20 KB | 1,056.81 KB |
| 10,000 | 5,085.98 KB | 10,805.23 KB | 10,478.28 KB |

The remaining overhead, about 2× at 10,000 tracked entities, is one extra change-detection pass that
Hindsight runs before the writer, so it can reject a save of a historical snapshot before anything is
written. To re-run only this benchmark:

```bash
dotnet run -c Release --project benchmarks/Hindsight.Benchmarks -- --filter '*ChangeTrackerOverheadBenchmarks*'
```
