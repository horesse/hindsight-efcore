Check that the documentation matches the code, and fix the gaps.

1. Read `src/**/PublicAPI.Unshipped.txt` and `PublicAPI.Shipped.txt` — that is the real public API.
2. Grep `docs/articles/*.md` and `README.md` for method and type names. Report:
   - symbols in the API that no article mentions (undocumented);
   - names in articles that don't exist in the API (stale or aspirational — check the pre-alpha
     warning: aspirational is allowed only under it).
3. Compare `CHANGELOG.md → Unreleased` with `git log --oneline <last tag>..HEAD` (`git describe --tags --abbrev=0`
   gives the tag). Every `feat:`/`fix:` commit with user-visible effect needs a line.
4. Run `dotnet tool restore && dotnet docfx docs/docfx.json --warningsAsErrors` and report warnings.

Then fix what you found in `docs/articles`, `README.md` and `CHANGELOG.md` following
`.claude/rules/docs.md`. Do not touch `docs/api/**` or `docs/design.md`. Finish with a list of files
changed and anything you deliberately left alone with the reason.
