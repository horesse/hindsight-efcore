import { fileURLToPath } from 'node:url'
import { defineConfig, type HeadConfig } from 'vitepress'
import { buildSidebar, type SidebarSection } from './sidebar'

const repo = 'https://github.com/horesse/hindsight-efcore'
const srcDir = fileURLToPath(new URL('..', import.meta.url))

// Every published build is one documentation *version*. CI sets both variables (see
// .github/workflows/docs.yml); a local `npm run dev` / `npm run build` sets neither and serves from '/'.
//   DOCS_VERSION    'nightly' or MAJOR.MINOR — the folder this build is published under
//   DOCS_SITE_ROOT  where the whole versioned site lives, e.g. '/hindsight-efcore/'
const version = process.env.DOCS_VERSION ?? 'dev'
const siteRoot = process.env.DOCS_SITE_ROOT ?? '/'
if (!/^(dev|nightly|\d+\.\d+)$/.test(version)) {
  throw new Error(`DOCS_VERSION must be 'nightly' or MAJOR.MINOR (e.g. 1.1), got '${version}'`)
}
if (!siteRoot.startsWith('/') || !siteRoot.endsWith('/')) {
  throw new Error(`DOCS_SITE_ROOT must start and end with '/', got '${siteRoot}'`)
}
const base = version === 'dev' ? '/' : `${siteRoot}${version}/`

// Sidebar sections, in order. The pages inside each one come from its folder (see sidebar.ts), so
// adding a page never edits this file — adding a *section* does, deliberately.
const sections: SidebarSection[] = [
  { text: 'Introduction', dir: 'introduction' },
  { text: 'Tutorials', dir: 'tutorials' },
  { text: 'Configuration', dir: 'configuration' },
  { text: 'Writing history', dir: 'writing' },
  { text: 'Querying history', dir: 'querying' },
  { text: 'Migrations', dir: 'migrations' },
  { text: 'Reference', dir: 'reference' },
]

const head: HeadConfig[] = [['link', { rel: 'icon', type: 'image/png', href: `${base}icon.png` }]]
if (version === 'nightly') {
  // Unreleased docs must never outrank the release a reader actually installed.
  head.push(['meta', { name: 'robots', content: 'noindex' }])
}

export default defineConfig({
  title: 'Hindsight',
  description: 'System-versioned temporal entities for EF Core on PostgreSQL',
  lang: 'en-US',
  base,
  head,
  cleanUrls: true,
  srcExclude: ['README.md', 'snippets/**', 'scripts/**'],
  vite: {
    define: {
      __DOCS_VERSION__: JSON.stringify(version),
      __DOCS_SITE_ROOT__: JSON.stringify(siteRoot),
    },
  },
  themeConfig: {
    logo: { src: '/icon.png', alt: 'Hindsight' },
    nav: [
      {
        text: 'Guide',
        link: '/introduction/what-is-hindsight',
        activeMatch: '^/(introduction|configuration|writing|querying|migrations)/',
      },
      { text: 'Tutorial', link: '/tutorials/audit-trail', activeMatch: '^/tutorials/' },
      { text: 'Reference', link: '/reference/limitations', activeMatch: '^/reference/' },
      { text: 'Release notes', link: `${repo}/releases` },
    ],
    sidebar: buildSidebar(srcDir, sections),
    outline: { level: [2, 3] },
    search: { provider: 'local' },
    socialLinks: [{ icon: 'github', link: repo }],
    editLink: { pattern: `${repo}/edit/master/docs/:path`, text: 'Edit this page on GitHub' },
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © Valentin Polhovski',
    },
  },
})
