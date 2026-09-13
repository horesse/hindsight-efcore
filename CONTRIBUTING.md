# Contributing

## Prerequisites

- .NET SDK 10 (see `global.json`)
- Docker — integration tests run PostgreSQL via Testcontainers
- Node.js 22 or later — only for the documentation site

## Build and test

```
dotnet build
dotnet test --project tests/Hindsight.Tests    # fast, no Docker
dotnet test --project tests/Hindsight.IntegrationTests   # needs Docker
dotnet format --verify-no-changes              # CI fails on formatting drift
npm --prefix docs ci                           # once: install the docs toolchain
npm --prefix docs run dev                      # docs at http://localhost:5173
npm --prefix docs run build                    # what CI runs: dead links and broken samples fail it
```

## Documentation

The site in `docs/` is VitePress, versioned per minor release (`1.1`, `1.2`, …) plus `nightly` from
`master`. Every PR updates the pages its change affects; the C# samples on them live in `docs/snippets`
and are compiled with the solution. How pages, samples and versions work: [`docs/README.md`](docs/README.md).

## Benchmarks

```
dotnet run -c Release --project benchmarks/Hindsight.Benchmarks -- --filter '*'
```

Needs Docker: the entry point starts one PostgreSQL container for the whole run and every benchmark
creates its own database on it. Not run in CI — it is manual, and the numbers in `README.md` and
`docs/reference/benchmarks.md` are refreshed by hand when the writers change. Narrow a run with
`--filter '*Insert*'`.

## Conventions

- Branch from `master`, open a PR. CI must be green.
- Commits: [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `chore:`), in English.
- The PR title is the change's line in the release notes (Release Drafter collects them; there is no
  changelog file). Write it for a user: `fix(query): reject ExecuteUpdate on AsOf() queries`, not
  `fix: handle edge case`. Label a PR `skip-changelog` to leave it out.
- Code and XML docs in English. Every public symbol has XML documentation.
- Public API changes go into `PublicAPI.Unshipped.txt` (the analyzer will tell you).
- Anything touching SQL gets an integration test. Migration DDL gets a Verify snapshot.
- Design changes go through `DESIGN.md` first.

## Releasing

1. Open the draft release Release Drafter keeps up to date from merged PR titles. Check its notes read
   well for users, and add an upgrade note if `docs/reference/upgrading.md` gained a section for this
   version.
2. Publish it with tag `vX.Y.Z` (or `vX.Y.Z-preview.N`). The `Release` workflow builds, tests, packs,
   waits for approval on the `nuget` environment and pushes to nuget.org via Trusted Publishing (OIDC,
   no stored API key). The tag is the version — MinVer reads it. For a stable tag it then publishes the
   documentation as version `X.Y` (a patch release republishes its minor); previews are documented by
   `nightly` only.
3. **Immediately after the release publishes** (same day, before any other PR merges — do not defer
   this): move every entry from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` and commit
   directly to `master`. Until this happens, `PublicApiAnalyzers` cannot distinguish "new API for the
   next release" from "API already in the hands of users," and for a stable (non-preview) release you
   should also uncomment `PackageValidationBaselineVersion` in `src/Directory.Build.props` to point at
   the version you just shipped — CI's `Pack` step then diffs every subsequent PR's public surface
   against the real published package. CI fails the build if a tagged release ships with
   `PublicAPI.Shipped.txt` unchanged (see `.github/workflows/ci.yml`), which is the backstop for
   forgetting this step.
