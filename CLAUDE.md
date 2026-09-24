# CLAUDE.md — Hindsight

System-versioned temporal entities for EF Core 10 on PostgreSQL. History tables are property-bag
entity types in the EF model (so migrations see them), history is written either by a
`SaveChangesInterceptor` or by a migration-generated plpgsql trigger, and `AsOf()` is LINQ over the
history table. Read `DESIGN.md` before touching anything non-trivial — it is the source of truth for
*why*; this file is the source of truth for *how to work here*.

## Golden rules

1. **No reflection on EF Core internals. Ever.** If a task needs `Microsoft.EntityFrameworkCore.*.Internal`
   types or reflection on private members, stop and say so. Propose the public-API fallback from
   `DESIGN.md` (`FromSql` for queries, annotation-driven SQL for migrations) or ask. A brittle binding
   to internals breaks on the next EF Core minor release; a missing feature does not.
2. **A silently wrong answer is worse than an exception.** `AsOf` + `Include`, TPH, unsupported
   column types — throw a clear `NotSupportedException` with the reason and the alternative.
   Never return a partial or guessed result from a historical query.
3. **History is never destroyed by a migration.** No `DropColumn`, `DropTable`, or narrowing
   `AlterColumn` on a history table unless the user wrote that migration by hand and says so.
4. **Half-open intervals `[valid_from, valid_to)`, `timestamptz`, UTC, `'infinity'` for the current
   version.** Every SQL and every test asserts this shape. No `DateTime.Now`, no `null` for "open".
5. **One timestamp per transaction.** Never `DateTime.UtcNow` per row. Interceptor mode: one value
   captured at the start of `SaveChanges` from `TimeProvider`. Trigger mode: `now()`.
6. **Anything touching SQL gets an integration test on real PostgreSQL (Testcontainers).** InMemory
   and SQLite prove nothing here. Migration DDL gets a Verify snapshot.
7. **Don't widen scope.** The limitations in `README.md` → Limitations and `DESIGN.md` are
   decided. If a change would cross them, open an issue (or describe one) instead of implementing.
8. **Public surface is deliberate.** Every new public symbol: XML doc, entry in
   `PublicAPI.Unshipped.txt`, justification in the PR description. Prefer `internal`.
9. **When unsure, ask before building.** A two-line question costs less than a wrong afternoon.
   Concretely: before starting an item from `DESIGN.md → Open questions`, summarize your plan and
   wait for confirmation.
10. **Documentation ships with the change, in the same PR.** A feature without its docs update is
    not done. See "Documentation" below for exactly which file changes when.

## Commands

```
dotnet build                                   # warnings are errors
dotnet format --verify-no-changes              # CI fails on drift; run `dotnet format` to fix
dotnet test --project tests/Hindsight.Tests    # unit, seconds, no Docker
dotnet test --project tests/Hindsight.Analyzers.Tests     # HDST001 analyzer, seconds, no Docker
dotnet test --project tests/Hindsight.IntegrationTests   # needs Docker; ~30s container start
npm --prefix docs ci                           # once: docs toolchain (Node 22+)
npm --prefix docs run build                    # docs: samples, dead links, anchors — what CI runs
npm --prefix docs run dev                      # docs preview at http://localhost:5173
cd samples/InsuranceSample && dotnet ef migrations add <Name>
```

Definition of done for any change: `dotnet build` + `dotnet format --verify-no-changes` + all three test
projects green, plus `npm --prefix docs run build` when `docs/` changed. Run them yourself before
reporting done; paste the failing output if they aren't.

## Layout

```
src/Hindsight.EntityFrameworkCore.PostgreSQL/   the package (namespace Hindsight)
src/Hindsight.Analyzers/                        HDST001; ships inside the package, not a separate one
tests/Hindsight.Tests/                          unit: model building, conventions, validation
tests/Hindsight.IntegrationTests/               Testcontainers: SQL, migrations, writers, queries
tests/Hindsight.Analyzers.Tests/                unit: what HDST001 does and does not flag
samples/                                        InsuranceSample drives the `dotnet ef` checks;
                                                ProductCatalogSample / TaskTrackerSample are the demos
benchmarks/Hindsight.Benchmarks/                BenchmarkDotNet; numbers go into README
docs/                                           VitePress site, versioned; how it works: docs/README.md
docs/snippets/                                  every C# sample on the site; compiled with the solution
DESIGN.md                                       decisions D1–D17 + open questions
```

## Conventions

- C# 14, nullable, `AnalysisLevel latest-recommended`, warnings as errors. Don't add `#pragma`
  or `NoWarn` to make a warning go away; fix it or explain why suppression is right in a comment.
- File-scoped namespaces, `_camelCase` private fields, `Async` suffix, primary constructors where
  they don't hurt readability.
- Annotation names live in `HindsightAnnotationNames`; never string-literal them elsewhere.
- Column and table names are snake_case in SQL (`valid_from`, `policies_history`).
- Commits: Conventional Commits, English, atomic. `feat(query): translate AsOf root replacement`.
  PR titles follow the same format — CI lints them (`.github/workflows/pr-title.yml` has the closed
  list of types and scopes) and the squash-merge uses the title as the commit message.
- A weekly canary builds against EF Core / Npgsql previews (`efcore-preview.yml`). An open issue
  labelled `efcore-preview` means an upcoming EF Core release breaks us — read it before touching
  anything in the affected area.
- Code, XML docs, README, docs/ — English. Explanations to the maintainer — Russian.
- Tests: `MethodOrScenario_Condition_Expectation` or a sentence in the `[Fact]` name; one behavior
  per test; arrange with the sample `Policy` entity unless the scenario needs something else.

## Documentation

The site is VitePress (`docs/`, <https://horesse.github.io/hindsight-efcore/>) and it is versioned:
`nightly` is rebuilt from `master` on every merge, and each stable release publishes its minor
(`1.1`, `1.2`, …) from the tag (`docs.yml`, called by `release.yml`). A docs change in a PR is therefore
live in `nightly` at once and in the numbered docs at the next release; document what the PR ships, in
the present tense. `ci.yml` builds the site on every PR: a dead link, a broken `#anchor`, an inline C#
block or a broken sample import fails it. Mechanics (pages, sidebar, samples, versions) are in
`docs/README.md`; writing rules in `.claude/rules/docs.md`.

- **There is no changelog file.** Release notes are drafted from PR titles by Release Drafter, so the
  PR title is the release-note line: write it for a user.
- **C# on the site is never inline.** It is a `#region` in `docs/snippets/*.cs`, compiled with the
  solution, imported into the page with `<<< @/snippets/File.cs#region`. Generated SQL is imported from
  the Verify snapshots, so pages change when the SQL does.
- `docs/reference/design.md` includes the root `DESIGN.md` — edit the root file.

What to update, by kind of change:

| you changed | update |
|---|---|
| a public symbol (new, renamed, removed, new parameter) | its XML doc; the page that shows it and its sample in `docs/snippets`; `PublicAPI.Unshipped.txt` |
| behavior visible to a user (what a migration generates, what a query returns, what throws) | the matching page; a PR title that says it |
| something users must do by hand when they upgrade | a section for the next version in `docs/reference/upgrading.md` |
| a design decision | `DESIGN.md` entry in place; the page that explained the old behavior |
| a limitation added or removed | `docs/reference/limitations.md` and `README.md` → Limitations |
| a new configuration option | its page under `docs/configuration/` or `docs/writing/`, with a sample in `docs/snippets` |
| a benchmark result | `README.md` and `docs/reference/benchmarks.md` tables |
| generated SQL (a Verify snapshot) | nothing to copy — pages import the snapshot; reread the prose around it |

If you change the API, `dotnet build` fails on every stale sample; grep `docs/` for the old name in
prose too. When you finish a task, list the doc files you touched; if the answer is "none" for a
user-visible change, that is a bug in the PR.

## Where things are decided

| question | answer lives in |
|---|---|
| why property-bag / two writers / no bulk interception | `DESIGN.md` D2–D4 |
| history column set and indexes | `DESIGN.md` D5 |
| what a migration does on add/remove/rename | `DESIGN.md` D6, `docs/migrations/schema-evolution.md` |
| what is out of scope | `README.md` → Limitations |
| how to release | `CONTRIBUTING.md` → Releasing |
| how the docs are versioned and published | `docs/README.md` → Versions |

If you change a decision, edit the `DESIGN.md` entry in the same PR. Don't append a correction below it.

## Things that look right but are wrong here

- Making history a `SharedTypeEntity<Policy>` — EF Core forbids a CLR type being both shared and
  non-shared. Property-bag (`Dictionary<string, object>`) is the way.
- Adding a unique index or FK to a history table.
- Filtering history with `valid_to IS NULL`.
- Catching `ExecuteUpdate`/`ExecuteDelete` in Interceptor mode via `IDbCommandInterceptor` — decided
  against (D4); Trigger mode covers it.
- Provider-neutral abstractions "for SQL Server later". PostgreSQL only, by design.
- "Fixing" a red integration test by switching it to InMemory.
