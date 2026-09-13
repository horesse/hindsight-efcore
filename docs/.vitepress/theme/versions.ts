// Client-side view of the published documentation versions.
//
// Which versions exist is only known at runtime: the 1.1 pages were built before 1.2 existed. So the
// list comes from `<site root>/versions.json`, rewritten by docs/scripts/publish-version.mjs on every
// publish; a build only knows its own version, injected by config.mts.
import { onMounted, shallowRef, type ShallowRef } from 'vue'

declare const __DOCS_VERSION__: string
declare const __DOCS_SITE_ROOT__: string

/** 'nightly', MAJOR.MINOR, or 'dev' for a local build that is not published anywhere. */
export const currentVersion: string = __DOCS_VERSION__
export const siteRoot: string = __DOCS_SITE_ROOT__

/** Schema 1 of versions.json — see docs/scripts/publish-version.mjs for the contract. */
export interface VersionsManifest {
  schema: 1
  latest: string | null
  stable: string[]
  nightly: boolean
}

let manifest: Promise<VersionsManifest | null> | undefined

function load(): Promise<VersionsManifest | null> {
  if (currentVersion === 'dev') return Promise.resolve(null)
  manifest ??= fetch(`${siteRoot}versions.json`, { cache: 'no-cache' })
    .then(response => (response.ok ? response.json() : null))
    .then(json => (isManifest(json) ? json : null))
    .catch(() => null)
  return manifest
}

function isManifest(value: unknown): value is VersionsManifest {
  const m = value as Partial<VersionsManifest> | null
  return !!m && m.schema === 1 && Array.isArray(m.stable) && typeof m.nightly === 'boolean'
}

/**
 * The published versions. `null` during SSR and until loaded, for a local build, and when the
 * manifest cannot be fetched — callers render nothing version-related in that case.
 */
export function useVersions(): ShallowRef<VersionsManifest | null> {
  const result = shallowRef<VersionsManifest | null>(null)
  onMounted(async () => {
    result.value = await load()
  })
  return result
}

export function isStable(version: string): boolean {
  return /^\d+\.\d+$/.test(version)
}

export function versionLabel(version: string): string {
  return isStable(version) ? `v${version}` : version
}

/** Home page of a published version. */
export function versionHome(version: string): string {
  return `${siteRoot}${version}/`
}

/**
 * Opens the page the reader is on in another version, or that version's home page when the page
 * does not exist there (added later, or renamed).
 */
export async function switchTo(version: string, currentBase: string): Promise<void> {
  const { pathname, hash } = window.location
  const page = pathname.startsWith(currentBase) ? pathname.slice(currentBase.length) : ''
  const target = versionHome(version) + page
  let exists = false
  if (page !== '') {
    try {
      exists = (await fetch(target, { method: 'HEAD' })).ok
    } catch {
      // Offline or blocked: fall back to the version's home page.
    }
  }
  window.location.href = exists ? target + hash : versionHome(version)
}
