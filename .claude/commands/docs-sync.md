Check that the documentation matches the code, and fix the gaps.

1. Read `src/**/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` — that is the real public API.
2. Grep the pages (`docs/**/*.md`, excluding `node_modules`), `docs/snippets/*.cs` and `README.md` for
   type and method names. Report:
   - public symbols that no page mentions (undocumented);
   - names on pages that don't exist in the API (stale).
3. For every `feat:` / `fix:` commit since the last tag
   (`git log --oneline $(git describe --tags --abbrev=0)..HEAD`) with a user-visible effect, check that
   a page describes the new behavior, and that a manual upgrade step, if there is one, is in
   `docs/reference/upgrading.md`.
4. Run `dotnet build` (compiles `docs/snippets`) and `npm --prefix docs ci && npm --prefix docs run build`
   (samples, dead links, anchors) and report failures.

Then fix what you found in `docs/` and `README.md`, following `.claude/rules/docs.md` and
`docs/README.md`. Do not touch `docs/reference/design.md`. Finish with a list of files changed and
anything you deliberately left alone with the reason.
