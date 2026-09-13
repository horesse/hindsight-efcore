#!/usr/bin/env bash
# Publishes one built docs version to the gh-pages branch. Called by .github/workflows/docs.yml.
#
#   publish-gh-pages.sh <build dir> <version> <source ref> <source sha>
#
# Environment:
#   DOCS_SITE_ROOT      e.g. /hindsight-efcore/
#   GH_TOKEN            token with contents: write        } used to build the remote URL,
#   GITHUB_REPOSITORY   owner/repo                        } unless DOCS_PAGES_REMOTE is set
#   DOCS_PAGES_REMOTE   optional remote URL override (local testing against a bare repository)
#
# gh-pages is build output, not history: every publish rewrites it as a single parentless commit,
# and where each version was built from is recorded in versions.json instead. Publishes of
# different versions can run at the same time, so the push is a compare-and-swap
# (--force-with-lease on the commit this run started from); a run that loses the race starts over
# from the winner's result. Layout and manifest: publish-version.mjs.
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: $0 <build dir> <version> <source ref> <source sha>" >&2
  exit 2
fi

dist=$(cd "$1" && pwd)
version=$2
ref=$3
sha=$4
script_dir=$(cd "$(dirname "$0")" && pwd)
remote=${DOCS_PAGES_REMOTE:-"https://x-access-token:${GH_TOKEN}@github.com/${GITHUB_REPOSITORY}.git"}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

export GIT_AUTHOR_NAME='github-actions[bot]'
export GIT_AUTHOR_EMAIL='41898282+github-actions[bot]@users.noreply.github.com'
export GIT_COMMITTER_NAME=$GIT_AUTHOR_NAME
export GIT_COMMITTER_EMAIL=$GIT_AUTHOR_EMAIL

for attempt in 1 2 3 4 5; do
  site="$work/site-$attempt"

  if base=$(git ls-remote --exit-code "$remote" refs/heads/gh-pages | cut -f1); then
    git clone --quiet --depth 1 --branch gh-pages --single-branch "$remote" "$site"
    # A publish may have landed between ls-remote and clone; the lease must name what we build on.
    base=$(git -C "$site" rev-parse HEAD)
  else
    base='' # first publish ever: the lease below then requires that gh-pages still does not exist
    git init --quiet "$site"
  fi

  node "$script_dir/publish-version.mjs" \
    --site "$site" --dist "$dist" --version "$version" \
    --site-root "$DOCS_SITE_ROOT" --ref "$ref" --sha "$sha"

  git -C "$site" add --all
  tree=$(git -C "$site" write-tree)
  commit=$(git -C "$site" commit-tree "$tree" -m "docs: publish ${version} from ${ref} (${sha})")

  if git -C "$site" push --quiet --force-with-lease="refs/heads/gh-pages:${base}" "$remote" "${commit}:refs/heads/gh-pages"; then
    echo "Published docs version ${version} from ${ref} (${sha})."
    exit 0
  fi

  echo "gh-pages changed while publishing ${version}; starting over (attempt ${attempt} of 5)." >&2
  sleep $((attempt * 5))
done

echo "::error::Could not publish docs version ${version}: gh-pages kept changing underneath." >&2
exit 1
