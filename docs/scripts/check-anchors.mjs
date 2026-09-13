#!/usr/bin/env node
// Fails the build on a link to a heading that does not exist. VitePress checks that a linked *page*
// exists, but not the #anchor after it, and anchors break silently whenever a heading is reworded.
// Runs after `vitepress build`, over the generated HTML, so it sees the ids VitePress really assigned.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const dist = fileURLToPath(new URL('../.vitepress/dist', import.meta.url))
// Same derivation as .vitepress/config.mts.
const version = process.env.DOCS_VERSION ?? 'dev'
const siteRoot = process.env.DOCS_SITE_ROOT ?? '/'
const base = version === 'dev' ? '/' : `${siteRoot}${version}/`

function* htmlFiles(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name)
    if (entry.isDirectory()) yield* htmlFiles(file)
    else if (entry.name.endsWith('.html')) yield file
  }
}

const idCache = new Map()
function idsIn(file) {
  if (!idCache.has(file)) {
    const html = fs.readFileSync(file, 'utf8')
    idCache.set(file, new Set([...html.matchAll(/\sid="([^"]+)"/g)].map(m => m[1])))
  }
  return idCache.get(file)
}

const unescape = s => s.replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#39;/g, "'")

/** The built file and fragment a link points at, or null when the link is not this check's business. */
function resolve(fromFile, href) {
  const hash = href.indexOf('#')
  if (hash < 0) return null
  const fragment = decodeURIComponent(href.slice(hash + 1))
  const target = href.slice(0, hash)
  if (!fragment) return null
  if (target === '') return { file: fromFile, fragment }
  if (!target.startsWith(base)) return null // external, or another documentation version

  const page = target.slice(base.length).replace(/\.html$/, '').replace(/\/$/, '')
  const candidates = page === '' ? ['index.html'] : [`${page}.html`, `${page}/index.html`]
  const file = candidates.map(c => path.join(dist, c)).find(f => fs.existsSync(f))
  return file ? { file, fragment } : null // a missing page is VitePress's dead-link check's business
}

if (!fs.existsSync(dist)) {
  console.error('check-anchors: no build output; run `vitepress build` first.')
  process.exit(1)
}

const errors = []
let checked = 0
for (const file of htmlFiles(dist)) {
  const html = fs.readFileSync(file, 'utf8')
  for (const [, raw] of html.matchAll(/<a\s[^>]*?href="([^"]+)"/g)) {
    const link = resolve(file, unescape(raw))
    if (!link) continue
    checked++
    if (!idsIn(link.file).has(link.fragment)) {
      errors.push(`${path.relative(dist, file)}: link to ${unescape(raw)} — no heading with id '${link.fragment}'`)
    }
  }
}

if (errors.length > 0) {
  for (const error of [...new Set(errors)]) console.error(error)
  console.error(`\ncheck-anchors: ${errors.length} broken anchor(s). Headings get ids from their text; see docs/README.md → Pages.`)
  process.exit(1)
}
console.log(`check-anchors: ${checked} anchor link(s) OK.`)
