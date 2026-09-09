# Contributing

## Prerequisites

- .NET SDK 10 (see `global.json`)
- Docker — integration tests run PostgreSQL via Testcontainers

## Build and test

```
dotnet build
dotnet test --project tests/Hindsight.Tests    # fast, no Docker
dotnet test --project tests/Hindsight.IntegrationTests   # needs Docker
dotnet format --verify-no-changes              # CI fails on formatting drift
dotnet docfx docs/docfx.json --serve            # docs at http://localhost:8080
```

## Conventions

- Branch from `master`, open a PR. CI must be green.
- Commits: [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `chore:`), in English.
- Code and XML docs in English. Every public symbol has XML documentation.
- Public API changes go into `PublicAPI.Unshipped.txt` (the analyzer will tell you).
- Anything touching SQL gets an integration test. Migration DDL gets a Verify snapshot.
- Design changes go through `DESIGN.md` first.

## Releasing

1. Move `Unreleased` in `CHANGELOG.md` under the new version.
2. Create a GitHub Release with tag `vX.Y.Z` (or `vX.Y.Z-preview.N`). The `Release` workflow builds,
   tests, packs, waits for approval on the `nuget` environment and pushes to nuget.org via Trusted
   Publishing (OIDC, no stored API key). The tag is the version — MinVer reads it.
3. Move `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt` in the next commit.
