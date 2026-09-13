import fs from 'node:fs'
import path from 'node:path'
import type { DefaultTheme } from 'vitepress'

export interface SidebarSection {
  /** Group title shown in the sidebar. */
  text: string
  /** Folder under docs/ holding the section's pages. */
  dir: string
}

/**
 * Builds the sidebar from the folder layout, so adding a page never touches a shared file (a
 * hand-kept sidebar list is a merge conflict for every pair of PRs that add a page).
 *
 * Every `*.md` file in a section's folder is a page. Frontmatter controls where it goes:
 *
 * ```yaml
 * ---
 * order: 20            # required: position inside the section (gaps are fine: 10, 20, 30)
 * sidebarTitle: AsOf   # optional: sidebar label; defaults to the page's first `# ` heading
 * ---
 * ```
 *
 * Pages with equal `order` are sorted by title. A page without `order`, or a section folder with no
 * pages, fails the build — the sidebar is never silently incomplete.
 */
export function buildSidebar(srcDir: string, sections: SidebarSection[]): DefaultTheme.SidebarItem[] {
  return sections.map(section => {
    const dir = path.join(srcDir, section.dir)
    if (!fs.existsSync(dir)) {
      throw new Error(`Sidebar section '${section.text}': folder docs/${section.dir} does not exist`)
    }

    const pages = fs
      .readdirSync(dir)
      .filter(file => file.endsWith('.md'))
      .map(file => readPage(section.dir, path.join(dir, file)))
      .sort((a, b) => a.order - b.order || a.text.localeCompare(b.text))

    if (pages.length === 0) {
      throw new Error(`Sidebar section '${section.text}': docs/${section.dir} has no pages`)
    }

    return {
      text: section.text,
      collapsed: false,
      items: pages.map(({ text, link }) => ({ text, link })),
    }
  })
}

interface Page {
  text: string
  link: string
  order: number
}

function readPage(dir: string, file: string): Page {
  const source = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n')
  const where = `docs/${dir}/${path.basename(file)}`
  const frontmatter = /^---\n([\s\S]*?)\n---\n/.exec(source)?.[1] ?? ''

  const order = Number(field(frontmatter, 'order'))
  if (!Number.isFinite(order)) {
    throw new Error(`${where}: frontmatter needs a numeric 'order' to place the page in the sidebar`)
  }

  const title = field(frontmatter, 'sidebarTitle') ?? /^# (.+)$/m.exec(source)?.[1]
  if (!title) {
    throw new Error(`${where}: needs a '# ' heading or a 'sidebarTitle' frontmatter key`)
  }

  return {
    // The theme renders sidebar labels as HTML: drop inline-code backticks from the heading and
    // escape the rest, so `History<T>` stays text instead of becoming a <t> element.
    text: title
      .replace(/`/g, '')
      .trim()
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;'),
    link: `/${dir}/${path.basename(file, '.md')}`,
    order,
  }
}

function field(frontmatter: string, key: string): string | undefined {
  const value = new RegExp(`^${key}:[ \\t]*(.+?)[ \\t]*$`, 'm').exec(frontmatter)?.[1]
  return value?.replace(/^(['"])(.*)\1$/, '$2')
}
