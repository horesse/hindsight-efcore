#!/usr/bin/env node
// Publishes one built documentation version into a working copy of the gh-pages branch.
// Called by publish-gh-pages.sh (CI); unit-tested by publish-version.test.mjs (`npm test`).
//
// gh-pages layout — GitHub Pages serves it as-is:
//   <version>/      one VitePress build per version: 'nightly', or MAJOR.MINOR ('1.1')
//   versions.json   the manifest below
//   index.html      redirects to the latest stable version (nightly before the first release)
//   404.html        resolves /latest/…, unversioned and pre-versioning links; otherwise explains
//                   that the page does not exist in that version
//   .nojekyll       serve files verbatim
//
// versions.json is a contract with every build ever published: pages built for 1.1 read the file
// that 1.3's publish wrote (docs/.vitepress/theme/versions.ts). Change it additively only; anything
// else needs a new `schema` number and a switcher that understands both.
//
//   {
//     "schema": 1,
//     "latest": "1.1",            // highest stable version, null before the first release
//     "stable": ["1.1", "1.0"],   // newest first
//     "nightly": true,
//     "sources": { "1.1": { "ref": "v1.1.0", "sha": "…", "publishedAt": "…" } }
//   }
//
// Usage:
//   node publish-version.mjs --site <gh-pages dir> --dist <build dir> --version <nightly|X.Y>
//                            --site-root </hindsight-efcore/> [--ref <git ref>] [--sha <commit>]
import fs from 'node:fs'
import path from 'node:path'
import { parseArgs } from 'node:util'
import { fileURLToPath } from 'node:url'

export const versionPattern = /^(nightly|\d+\.\d+)$/

// URLs of the pre-versioning (DocFX) site, relative to the site root and without `.html`, mapped to
// the page that replaced them. The 404 page sends them to that page in the latest version.
export const legacyPages = {
  index: '',
  articles: 'introduction/what-is-hindsight',
  'articles/getting-started': 'introduction/getting-started',
  'articles/concepts': 'introduction/concepts',
  'articles/configuration': 'configuration/temporal-entities',
  'articles/querying': 'querying/as-of',
  'articles/history-writers': 'writing/history-writers',
  'articles/schema-evolution': 'migrations/schema-evolution',
  'articles/limitations': 'reference/limitations',
  design: 'reference/design',
}

/** Orders MAJOR.MINOR versions ascending (numerically: 1.10 is newer than 1.9). */
export function compareVersions(a, b) {
  const [aMajor, aMinor] = a.split('.').map(Number)
  const [bMajor, bMinor] = b.split('.').map(Number)
  return aMajor - bMajor || aMinor - bMinor
}

export function publish({ site, dist, version, siteRoot, ref = null, sha = null, now = new Date() }) {
  if (!versionPattern.test(version)) {
    throw new Error(`version must be 'nightly' or MAJOR.MINOR (e.g. 1.1), got '${version}'`)
  }
  if (!/^\/(.+\/)?$/.test(siteRoot)) {
    throw new Error(`site root must start and end with '/', got '${siteRoot}'`)
  }
  if (!fs.existsSync(path.join(dist, 'index.html'))) {
    throw new Error(`${dist} is not a VitePress build (no index.html)`)
  }

  fs.mkdirSync(site, { recursive: true })
  const manifest = readManifest(site)

  const target = path.join(site, version)
  fs.rmSync(target, { recursive: true, force: true })
  fs.cpSync(dist, target, { recursive: true })

  if (version === 'nightly') {
    manifest.nightly = true
  } else if (!manifest.stable.includes(version)) {
    manifest.stable.push(version)
  }
  manifest.stable.sort((a, b) => compareVersions(b, a))
  manifest.latest = manifest.stable[0] ?? null
  manifest.sources[version] = { ref, sha, publishedAt: now.toISOString() }

  const { schema, latest, stable, nightly, sources, ...unknown } = manifest
  const ordered = { schema, latest, stable, nightly, sources, ...unknown } // keep keys a later schema-1 writer added
  fs.writeFileSync(path.join(site, 'versions.json'), `${JSON.stringify(ordered, null, 2)}\n`)
  fs.writeFileSync(path.join(site, 'index.html'), indexPage(manifest, siteRoot))
  fs.writeFileSync(path.join(site, '404.html'), notFoundPage(manifest, siteRoot))
  fs.writeFileSync(path.join(site, '.nojekyll'), '')
  return manifest
}

function readManifest(site) {
  const file = path.join(site, 'versions.json')
  if (!fs.existsSync(file)) return { schema: 1, latest: null, stable: [], nightly: false, sources: {} }

  const manifest = JSON.parse(fs.readFileSync(file, 'utf8'))
  if (manifest.schema !== 1) {
    throw new Error(`${file} has schema ${manifest.schema}; this script only writes schema 1`)
  }
  return { sources: {}, ...manifest }
}

/** The version a bare or /latest/ link should land on. */
function defaultVersion(manifest) {
  return manifest.latest ?? (manifest.nightly ? 'nightly' : null)
}

function indexPage(manifest, siteRoot) {
  const target = `${siteRoot}${defaultVersion(manifest)}/`
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Hindsight documentation</title>
<meta http-equiv="refresh" content="0; url=${target}">
<script>location.replace(${JSON.stringify(target)} + location.hash)</script>
</head>
<body>
<p>Redirecting to <a href="${target}">${target}</a>…</p>
</body>
</html>
`
}

function notFoundPage(manifest, siteRoot) {
  const config = {
    root: siteRoot,
    fallback: defaultVersion(manifest),
    versions: [...manifest.stable, ...(manifest.nightly ? ['nightly'] : [])],
    legacy: legacyPages,
  }
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>Page not found · Hindsight</title>
<style>
  body { margin: 0; font: 16px/1.6 system-ui, sans-serif; color: #1f2937; background: #f8fafc; }
  main { max-width: 36rem; margin: 18vh auto; padding: 0 1.5rem; }
  h1 { font-size: 1.5rem; }
  a { color: #0f766e; }
  @media (prefers-color-scheme: dark) { body { color: #e5e7eb; background: #111827; } a { color: #5eead4; } }
</style>
<script>
  // Generated by docs/scripts/publish-version.mjs.
  (function (c) {
    var path = location.pathname;
    if (path.indexOf(c.root) !== 0 || !c.fallback) return;
    var rest = path.slice(c.root.length);
    var slash = rest.indexOf('/');
    var first = slash < 0 ? rest : rest.slice(0, slash);
    var go = function (version, page) {
      location.replace(c.root + version + '/' + page + location.search + location.hash);
    };
    if (first === 'latest') return go(c.fallback, slash < 0 ? '' : rest.slice(slash + 1));
    if (c.versions.indexOf(first) >= 0) {
      window.hindsightMissingIn = first; // a real version; this page just does not exist in it
      return;
    }
    var page = rest.replace(/\\.html$/, '').replace(/\\/$/, '');
    if (Object.prototype.hasOwnProperty.call(c.legacy, page)) return go(c.fallback, c.legacy[page]);
    if (/^api(\\/|$)/.test(page)) return go(c.fallback, '');
    go(c.fallback, rest);
  })(${JSON.stringify(config)});
</script>
</head>
<body>
<main>
  <h1>Page not found</h1>
  <p id="message">This page does not exist in this version of the Hindsight documentation.
    It may have been added in a later version, or moved.</p>
  <p><a id="home" href="${siteRoot}">Go to the documentation home</a></p>
</main>
<script>
  if (window.hindsightMissingIn) {
    document.getElementById('home').href = ${JSON.stringify(siteRoot)} + window.hindsightMissingIn + '/';
    document.getElementById('home').textContent = 'Go to the home page of ' +
      (/^\\d/.test(window.hindsightMissingIn) ? 'v' : '') + window.hindsightMissingIn;
  }
</script>
</body>
</html>
`
}

function main() {
  const { values } = parseArgs({
    options: {
      site: { type: 'string' },
      dist: { type: 'string' },
      version: { type: 'string' },
      'site-root': { type: 'string' },
      ref: { type: 'string' },
      sha: { type: 'string' },
    },
  })
  for (const required of ['site', 'dist', 'version', 'site-root']) {
    if (!values[required]) throw new Error(`--${required} is required`)
  }
  const manifest = publish({
    site: values.site,
    dist: values.dist,
    version: values.version,
    siteRoot: values['site-root'],
    ref: values.ref ?? null,
    sha: values.sha ?? null,
  })
  console.log(`Published ${values.version}. Stable: [${manifest.stable.join(', ')}], latest: ${manifest.latest ?? 'none'}, nightly: ${manifest.nightly}.`)
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main()
  } catch (error) {
    console.error(`publish-version: ${error.message}`)
    process.exit(1)
  }
}
