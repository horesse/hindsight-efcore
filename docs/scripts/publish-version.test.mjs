import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { afterEach, beforeEach, describe, test } from 'node:test'
import { compareVersions, publish } from './publish-version.mjs'

const siteRoot = '/hindsight-efcore/'
let work

beforeEach(() => {
  work = fs.mkdtempSync(path.join(os.tmpdir(), 'hindsight-docs-'))
})

afterEach(() => {
  fs.rmSync(work, { recursive: true, force: true })
})

/** A fake VitePress build containing `files` (name → content). */
function build(name, files = {}) {
  const dist = path.join(work, 'dist', name)
  fs.mkdirSync(dist, { recursive: true })
  fs.writeFileSync(path.join(dist, 'index.html'), `<html>${name}</html>`)
  for (const [file, content] of Object.entries(files)) fs.writeFileSync(path.join(dist, file), content)
  return dist
}

const site = () => path.join(work, 'site')
const read = file => fs.readFileSync(path.join(site(), file), 'utf8')
const manifest = () => JSON.parse(read('versions.json'))

describe('publish', () => {
  test('nightly before any release: no latest, root redirects to nightly', () => {
    publish({ site: site(), dist: build('nightly'), version: 'nightly', siteRoot })

    assert.deepEqual(manifest().stable, [])
    assert.equal(manifest().latest, null)
    assert.equal(manifest().nightly, true)
    assert.match(read('index.html'), /url=\/hindsight-efcore\/nightly\//)
    assert.equal(read('nightly/index.html'), '<html>nightly</html>')
    assert.ok(fs.existsSync(path.join(site(), '.nojekyll')))
  })

  test('latest is the highest stable version, whatever order they are published in', () => {
    publish({ site: site(), dist: build('a'), version: '1.1', siteRoot })
    publish({ site: site(), dist: build('b'), version: '1.10', siteRoot })
    publish({ site: site(), dist: build('c'), version: '1.9', siteRoot })

    assert.deepEqual(manifest().stable, ['1.10', '1.9', '1.1'])
    assert.equal(manifest().latest, '1.10')
    assert.match(read('index.html'), /url=\/hindsight-efcore\/1\.10\//)
  })

  test('republishing a version replaces its folder instead of merging into it', () => {
    publish({ site: site(), dist: build('first', { 'old.html': 'stale' }), version: '1.1', siteRoot })
    publish({ site: site(), dist: build('second'), version: '1.1', siteRoot })

    assert.equal(read('1.1/index.html'), '<html>second</html>')
    assert.equal(fs.existsSync(path.join(site(), '1.1', 'old.html')), false)
    assert.deepEqual(manifest().stable, ['1.1'])
  })

  test('publishing one version leaves the others untouched', () => {
    publish({ site: site(), dist: build('one-one'), version: '1.1', siteRoot })
    publish({ site: site(), dist: build('night'), version: 'nightly', siteRoot })

    assert.equal(read('1.1/index.html'), '<html>one-one</html>')
    assert.equal(manifest().latest, '1.1')
    assert.equal(manifest().nightly, true)
  })

  test('records where each version was built from', () => {
    const now = new Date('2026-09-14T10:00:00Z')
    publish({ site: site(), dist: build('x'), version: '1.1', siteRoot, ref: 'v1.1.0', sha: 'abc123', now })

    assert.deepEqual(manifest().sources['1.1'], { ref: 'v1.1.0', sha: 'abc123', publishedAt: '2026-09-14T10:00:00.000Z' })
  })

  test('404 page knows the published versions and where /latest/ points', () => {
    publish({ site: site(), dist: build('x'), version: '1.1', siteRoot })
    publish({ site: site(), dist: build('y'), version: 'nightly', siteRoot })

    const page = read('404.html')
    assert.match(page, /"fallback":"1\.1"/)
    assert.match(page, /"versions":\["1\.1","nightly"\]/)
    assert.match(page, /"articles\/configuration":"configuration\/temporal-entities"/)
  })

  for (const version of ['1.1.0', 'v1.1', 'latest', '1', '']) {
    test(`rejects version '${version}'`, () => {
      assert.throws(() => publish({ site: site(), dist: build('x'), version, siteRoot }), /version must be/)
    })
  }

  test('rejects a site root that does not start and end with a slash', () => {
    assert.throws(() => publish({ site: site(), dist: build('x'), version: '1.1', siteRoot: 'hindsight' }), /site root/)
  })

  test('rejects a dist folder that is not a build', () => {
    const empty = path.join(work, 'empty')
    fs.mkdirSync(empty)
    assert.throws(() => publish({ site: site(), dist: empty, version: '1.1', siteRoot }), /not a VitePress build/)
  })

  test('refuses to rewrite a manifest with an unknown schema', () => {
    fs.mkdirSync(site(), { recursive: true })
    fs.writeFileSync(path.join(site(), 'versions.json'), JSON.stringify({ schema: 2 }))
    assert.throws(() => publish({ site: site(), dist: build('x'), version: '1.1', siteRoot }), /schema 2/)
  })
})

describe('compareVersions', () => {
  test('compares numerically, not lexically', () => {
    assert.ok(compareVersions('1.10', '1.9') > 0)
    assert.ok(compareVersions('2.0', '1.99') > 0)
    assert.equal(compareVersions('1.1', '1.1'), 0)
  })
})
