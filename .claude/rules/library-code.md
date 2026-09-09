---
paths:
  - "src/**/*.cs"
---

# Rules for library code (`src/`)

- Every `public` type and member has an XML doc comment that says what it does *and* when to use it.
  A comment that restates the name (`/// Gets the name.`) is not documentation.
- Prefer `internal`. Public is a compatibility promise; `PublicAPI.Unshipped.txt` must be updated
  for every new public symbol — run the RS0016 code fix or edit by hand.
- No `System.Reflection` on the hot path. Everything derivable from the model (column mappings,
  property accessors, history table names) is computed once at model finalization and cached on the
  entity type as an annotation or a runtime annotation.
- No `Microsoft.EntityFrameworkCore.*.Internal` usings. If IntelliSense offers one, that's the signal
  to stop and report per CLAUDE.md rule 1.
- `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` at every public
  entry point; no manual `if (x is null) throw`.
- Exceptions thrown to users are `InvalidOperationException` (model/config mistakes) or
  `NotSupportedException` (deliberate non-goals). Message: what is wrong, on which entity, what to do.
  Example: `Entity 'Policy' is temporal but has no primary key. Temporal entities require a key; use HasKey() or mark the entity as keyless and remove IsTemporal().`
- SQL is generated through `Npgsql` / EF Core `ISqlGenerationHelper` (quoting, escaping) — never string
  concatenation of raw identifiers.
- Async methods take a `CancellationToken` and pass it through. Provide the sync counterpart only where
  EF Core has one (`SaveChanges` / `SaveChangesAsync`).
