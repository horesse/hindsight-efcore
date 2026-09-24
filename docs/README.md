# Documentation site

Source of <https://horesse.github.io/hindsight-efcore/>, built with [VitePress](https://vitepress.dev).
This file is for people (and agents) changing the docs; it is not part of the site. How to *write* a
page, meaning audience, tone and structure, is in [`.claude/rules/docs.md`](../.claude/rules/docs.md).

## Run it locally

Needs Node.js 22 or later.

```bash
cd docs
npm ci
npm run dev       # http://localhost:5173, reloads as you edit
npm run build     # what CI runs: snippet check + production build; a dead link fails it
npm run preview   # serve the production build
npm test          # tests for the publishing script
```

## Layout

```
docs/
  index.md                    home page
  introduction/ tutorials/ configuration/ writing/ querying/ migrations/ reference/
                              one folder per sidebar section, one .md file per page
  snippets/                   every C# sample on the site (a project in Hindsight.slnx)
  public/                     static files served as-is (icon.png, images/ for screenshots)
  .vitepress/config.mts       site config; the list of sidebar sections
  .vitepress/sidebar.ts       builds each section's pages from its folder
  .vitepress/theme/           version switcher and "not the latest version" banner
  scripts/check-snippets.mjs  runs before every build; see Code samples
  scripts/check-anchors.mjs   runs after every build; see Pages
  scripts/publish-*.{mjs,sh}  publishing a version to gh-pages; see Versions
```

## Pages

A page is a Markdown file in a section folder. Its frontmatter places it in the sidebar:

```yaml
---
order: 20            # required: position in the section; leave gaps (10, 20, 30)
sidebarTitle: AsOf   # optional: sidebar label, defaults to the page's `# ` heading
---
```

Adding a page never touches a shared file, so parallel PRs don't conflict over the sidebar. Adding a
*section* means a new folder and an entry in `sections` in `.vitepress/config.mts`.

- **Links** are site-absolute and without extension: `[AsOf](/querying/as-of#composing)`. VitePress
  adds the version prefix and fails the build on a link to a page that does not exist;
  `scripts/check-anchors.mjs` fails it on a link to a `#heading` that does not exist. A heading's id
  comes from its text; pin one that other pages link to with `## Heading text {#stable-id}`.
- **Callouts**: `::: tip`, `::: warning`, `::: danger`, `::: details Title`.
- **Generic types in prose go in backticks.** Pages are Vue templates, so a bare `History<T>` is
  parsed as an HTML tag.

## Code samples

C# is never written inline in a page; the build rejects a ` ```csharp ` block. Every sample is a
region of a file in `snippets/`:

```csharp
#region as-of
var policy = await db.Policies.AsOf(claim.OccurredAt).SingleOrDefaultAsync(p => p.Id == claim.PolicyId);
#endregion as-of
```

and the page imports it:

```md
<<< @/snippets/Querying.cs#as-of
```

- Repeat the name after `#endregion`; VitePress needs it to find the end.
- `snippets/` is part of the solution, so `dotnet build` compiles every sample against the current
  public API. Samples are compiled, never run; behavior is covered by the integration tests.
- `scripts/check-snippets.mjs` fails the build on an import of a missing file or region, and on a
  region that no page imports.
- Whole files from elsewhere in the repository work too: `<<< @/../samples/InsuranceSample/Policy.cs`.
- To show SQL that Hindsight generates, import the Verify snapshot the integration tests pin, such as
  `<<< @/../tests/Hindsight.IntegrationTests/TriggerDdlTests.Trigger_ddl_has_the_expected_shape.verified.sql`.
  The page then changes exactly when the SQL does.

## Versions

The site holds one build per documentation version, side by side:

| version | built from | published by |
|---|---|---|
| `nightly` | `master` | every push to `master` that touches the docs (`docs.yml`) |
| `X.Y`, e.g. `1.1` | the release tag `vX.Y.Z` | the Release workflow, after the package is on NuGet (`release.yml`) |

- A patch release republishes its minor: `v1.1.3` replaces the `1.1` docs.
- Preview releases get no version of their own; their docs are `nightly`.
- `/latest/…` and links without a version redirect to the newest stable version.
- Links into the old DocFX site (`/articles/…`) redirect to the page that replaced them.

**Every PR updates the docs it affects.** The change goes live in `nightly` when the PR merges, and in
a numbered version with the next release. Released versions never change underneath their readers.

### Fixing the docs of a released version

1. Branch `release/X.Y` from the latest `vX.Y.*` tag, if it does not exist yet.
2. Commit the fix there (cherry-pick it from `master` if it applies to both).
3. Run the **Docs** workflow manually with `ref: release/X.Y` and `version: X.Y`.

### How publishing works

`scripts/publish-gh-pages.sh` puts a build into its folder on the `gh-pages` branch, and
`scripts/publish-version.mjs` rewrites the files at the root: `versions.json` (read at runtime by
every version's switcher, so its format is a contract; see the script's header), `index.html` and
`404.html`. `gh-pages` holds build output only and is rewritten as a single commit on every publish;
`versions.json` records the ref and commit each version was built from.

### One-time setup

- **Settings → Pages → Build and deployment → Source: Deploy from a branch**, branch `gh-pages`,
  folder `/ (root)`. The first run of the Docs workflow creates the branch.
- Publish the first numbered version by hand: run the **Docs** workflow with the ref that documents
  it and its version.
