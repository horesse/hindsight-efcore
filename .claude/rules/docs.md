---
paths:
  - "docs/**"
  - "README.md"
---

# Rules for documentation

How the site works (pages, sidebar, code samples, versions) is in `docs/README.md`. These are the rules
for what goes on a page.

- Audience: a .NET developer who has used EF Core migrations and knows what a transaction is, but has
  never heard of temporal tables. Lead with the problem, then the code, then the caveat.
- One page per topic, in the section folder it belongs to, with `order` in its frontmatter. A page
  should read in a few minutes; when one outgrows that, split it. Don't create a page for something
  that fits as a section of an existing one.
- Write for users, not maintainers: what it does, how to use it, what throws and what to do then.
  Internal type names, PR history ("before this fix…") and implementation walkthroughs belong in
  `DESIGN.md`, not on a page.
- Short paragraphs. Tables for comparisons and option lists. `::: tip` / `::: warning` / `::: danger`
  for the one thing a reader must not miss, not for emphasis in general.
- Every C# sample is a region in `docs/snippets` (compiled with the solution; the build rejects inline
  C#). It uses the `Policy` entity from the sample project and real names from `PublicAPI.*.txt`, and
  is complete enough to paste: usings implied, no `...` inside a lambda unless the page says what goes
  there.
- Show SQL that Hindsight generates by importing its Verify snapshot, never by retyping it.
- Document what the PR ships, in the present tense. The page goes live in `nightly` on merge and in the
  numbered docs with the next release, so there is no "coming in 1.x" and no pre-release warning to
  remember to remove later.
- A manual step users must take when upgrading goes into `docs/reference/upgrading.md`, under the
  version that needs it.
- Links are site-absolute (`/querying/as-of#composing`). Before rewording a heading that other pages
  link to, pin its id (`## New wording {#old-id}`); the build fails on a broken anchor.
- Generic types in prose go in backticks: pages are Vue templates, and a bare `History<T>` is an HTML
  tag to them.
- `README.md` is the NuGet landing page: keep it under ~150 lines (comparison table, three code blocks,
  non-goals) and link into the site (`https://horesse.github.io/hindsight-efcore/latest/…`) for depth.
- Never edit `docs/reference/design.md`: it includes the root `DESIGN.md`.
