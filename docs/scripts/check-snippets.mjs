#!/usr/bin/env node
// Enforces "every code sample compiles" for the docs site. Runs before every `npm run build`.
//
// C# shown in the docs is never written inline: it lives in docs/snippets — a project in the
// solution, so `dotnet build` compiles it against the current public API — and a page imports a
// region of it with VitePress's snippet syntax:
//
//     <<< @/snippets/Querying.cs#as-of
//
// VitePress on its own imports the *whole file* when the region name is wrong, and crashes with a
// bare ENOENT on a missing file. This script fails the build with a precise message instead when:
//   - a `<<<` import names a file that does not exist, or a region that file does not define;
//   - an `<!--@include: … -->` names a file that does not exist;
//   - a page contains an inline ```csharp / ```cs code block;
//   - a `#region` in docs/snippets is not imported by any page (dead sample code nobody reads).
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const docsDir = fileURLToPath(new URL('..', import.meta.url))
const repoDir = path.resolve(docsDir, '..')
const snippetsDir = path.join(docsDir, 'snippets')
const skippedDirs = new Set(['node_modules', '.vitepress', 'snippets', 'scripts', 'public'])

// Import syntax and region markers exactly as VitePress 1.x parses them
// (src/node/markdown/plugins/snippet.ts), so "passes here" means "VitePress finds it".
const rawPathRegexp =
  /^(.+?(?:(?:\.([a-z0-9]+))?))(?:(#[\w-]+))?(?: ?(?:{(\d+(?:[,-]\d+)*)? ?(\S+)? ?(\S+)?}))? ?(?:\[(.+)\])?$/
const regionRegexps = [
  /^\/\/ ?#?((?:end)?region) ([\w*-]+)$/,
  /^\/\* ?#((?:end)?region) ([\w*-]+) ?\*\/$/,
  /^#pragma ((?:end)?region) ([\w*-]+)$/,
  /^<!-- #?((?:end)?region) ([\w*-]+) -->$/,
  /^#((?:End )Region) ([\w*-]+)$/,
  /^::#((?:end)region) ([\w*-]+)$/,
  /^# ?((?:end)?region) ([\w*-]+)$/,
]
const includeRegexp = /<!--\s*@include:\s*(.*?)\s*-->/g
const csharpRegionRegexp = /^\s*#region ([\w*-]+)\s*$/

function hasRegion(lines, name) {
  const test = (line, regexp, end) => {
    const [full, tag, found] = regexp.exec(line.trim()) ?? []
    return !!full && found === name && (end ? /^[Ee]nd ?[rR]egion$/ : /^[rR]egion$/).test(tag)
  }
  for (const [index, line] of lines.entries()) {
    const regexp = regionRegexps.find(r => test(line, r, false))
    if (regexp) return lines.slice(index + 1).some(l => test(l, regexp, true))
  }
  return false
}

function* markdownFiles(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!skippedDirs.has(entry.name)) yield* markdownFiles(path.join(dir, entry.name))
    } else if (entry.name.endsWith('.md') && !(dir === docsDir && entry.name === 'README.md')) {
      yield path.join(dir, entry.name)
    }
  }
}

function* csharpFiles(dir) {
  if (!fs.existsSync(dir)) return
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name !== 'bin' && entry.name !== 'obj') yield* csharpFiles(path.join(dir, entry.name))
    } else if (entry.name.endsWith('.cs')) {
      yield path.join(dir, entry.name)
    }
  }
}

const rel = file => path.relative(repoDir, file).split(path.sep).join('/')
const readLines = file => fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n').split('\n')
const resolveFrom = (page, target) =>
  target.startsWith('@') ? path.join(docsDir, target.slice(1)) : path.resolve(path.dirname(page), target)

const errors = []
const report = (file, line, message) => errors.push({ file: rel(file), line, message })

const imported = new Set() // "<absolute file>#<region>", or "<absolute file>#*" for a whole-file import

for (const page of markdownFiles(docsDir)) {
  const lines = readLines(page)
  let fence = null // the opening fence ("```", "~~~~", …) while inside a code block

  lines.forEach((line, index) => {
    const lineNo = index + 1
    const fenceMatch = /^\s{0,3}(`{3,}|~{3,})\s*([^\s`]*)/.exec(line)
    if (fence) {
      if (fenceMatch && fenceMatch[1][0] === fence[0] && fenceMatch[1].length >= fence.length) fence = null
      return
    }
    if (fenceMatch) {
      fence = fenceMatch[1]
      if (/^(csharp|cs|c#)$/i.test(fenceMatch[2])) {
        report(page, lineNo, 'inline C# block: move the code into docs/snippets and import it with `<<< @/snippets/<File>.cs#<region>`')
      }
      return
    }

    const trimmed = line.trim()
    if (trimmed.startsWith('<<<')) {
      const raw = trimmed.slice(3).trim()
      const [, filepath = '', , region = ''] = rawPathRegexp.exec(raw) ?? []
      const file = resolveFrom(page, filepath)
      if (!fs.existsSync(file) || !fs.statSync(file).isFile()) {
        report(page, lineNo, `<<< ${raw}: file not found (${rel(file)})`)
        return
      }
      const name = region.slice(1)
      if (name && !hasRegion(readLines(file), name)) {
        report(page, lineNo, `<<< ${raw}: ${rel(file)} has no region '${name}' (#region ${name} … #endregion ${name})`)
        return
      }
      imported.add(`${file}#${name || '*'}`)
    }

    for (const [, target] of line.matchAll(includeRegexp)) {
      const file = resolveFrom(page, target.replace(/(#[\w-]+)?(\{\d*,\d*\})?$/, ''))
      if (!fs.existsSync(file)) report(page, lineNo, `@include ${target}: file not found (${rel(file)})`)
    }
  })
}

for (const file of csharpFiles(snippetsDir)) {
  if (imported.has(`${file}#*`)) continue
  readLines(file).forEach((line, index) => {
    const name = csharpRegionRegexp.exec(line)?.[1]
    if (name && !imported.has(`${file}#${name}`)) {
      report(file, index + 1, `region '${name}' is not imported by any page; delete it or show it with <<< @/snippets/${path.relative(snippetsDir, file).split(path.sep).join('/')}#${name}`)
    }
  })
}

if (errors.length > 0) {
  for (const { file, line, message } of errors) {
    console.error(process.env.GITHUB_ACTIONS ? `::error file=${file},line=${line}::${message}` : `${file}:${line}: ${message}`)
  }
  console.error(`\ncheck-snippets: ${errors.length} problem(s). See docs/README.md → Code samples.`)
  process.exit(1)
}
console.log(`check-snippets: ${imported.size} snippet import(s) OK.`)
