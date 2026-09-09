---
paths:
  - "docs/**/*.md"
  - "docs/**/*.yml"
  - "README.md"
  - "CHANGELOG.md"
---

# Rules for documentation

- Audience: a .NET developer who has used EF Core migrations and knows what a transaction is, but
  has never heard of temporal tables. Lead with the problem, then the code, then the caveat.
- One article per topic; add to `docs/articles/toc.yml` when adding a page. Don't create a page for
  something that fits as a section in an existing one.
- Every code sample is complete enough to paste: usings implied, `Policy` entity from the sample
  project, real method names from `PublicAPI.*.txt`. No `...` inside a lambda unless the article says
  what goes there.
- State the current status honestly. While the feature isn't implemented, the page keeps the
  `> [!WARNING]` pre-alpha note; remove it in the PR that ships the feature — not before.
- Cross-reference API with `<xref:Hindsight.TypeName>` / `<xref:Hindsight.TypeName.Method*>`; DocFX
  fails the build on an unresolved xref, which is the point.
- `README.md` is the NuGet landing page: keep it under ~150 lines, comparison table + 3 code blocks +
  non-goals. Depth goes to `docs/articles`, not README.
- `CHANGELOG.md` follows Keep a Changelog: Added / Changed / Deprecated / Removed / Fixed under
  `Unreleased`. One line per user-visible change, in the user's words ("`AsOf` now composes with
  `Include` for reference navigations"), not the implementation's ("refactored root visitor").
- Never edit `docs/api/**` — it is generated. Never edit `docs/design.md` — it includes `DESIGN.md`.
