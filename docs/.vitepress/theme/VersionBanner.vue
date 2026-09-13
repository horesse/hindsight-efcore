<script setup lang="ts">
import { computed } from 'vue'
import { useData } from 'vitepress'
import { currentVersion, isStable, switchTo, useVersions, versionHome, versionLabel } from './versions'

/** `home`: rendered above the home-page hero rather than inside a doc page. */
const props = defineProps<{ home?: boolean }>()

const { site } = useData()
const manifest = useVersions()
const latest = computed(() => manifest.value?.latest ?? null)

// A nightly build knows what it is at build time; whether a stable build is outdated is only known
// once versions.json has loaded (a newer minor may have been published since this build).
const kind = computed<'nightly' | 'outdated' | null>(() => {
  if (currentVersion === 'nightly') return 'nightly'
  if (isStable(currentVersion) && latest.value && latest.value !== currentVersion) return 'outdated'
  return null
})

function goToLatest() {
  if (latest.value) void switchTo(latest.value, site.value.base)
}
</script>

<template>
  <div v-if="kind" class="version-banner" :class="[kind, { home: props.home }]" role="note">
    <template v-if="kind === 'nightly'">
      <strong>Nightly documentation.</strong>
      These pages describe unreleased changes on <code>master</code>.
      <template v-if="latest">
        For the current release, see
        <a :href="versionHome(latest)" @click.prevent="goToLatest">{{ versionLabel(latest) }}</a>.
      </template>
    </template>
    <template v-else>
      <strong>You are reading the documentation for {{ versionLabel(currentVersion) }}.</strong>
      The latest release is
      <a :href="versionHome(latest ?? '')" @click.prevent="goToLatest">{{ versionLabel(latest ?? '') }}</a>.
    </template>
  </div>
</template>

<style scoped>
.version-banner {
  margin-bottom: 24px;
  padding: 12px 16px;
  border: 1px solid transparent;
  border-radius: 8px;
  font-size: 14px;
  line-height: 24px;
  color: var(--vp-c-text-1);
}

.version-banner.nightly {
  border-color: var(--vp-c-tip-soft);
  background-color: var(--vp-c-tip-soft);
}

.version-banner.outdated {
  border-color: var(--vp-c-warning-soft);
  background-color: var(--vp-c-warning-soft);
}

.version-banner.home {
  max-width: 1152px;
  margin: 24px auto 0;
}

.version-banner a {
  font-weight: 500;
  color: var(--vp-c-brand-1);
  text-decoration: underline;
  text-underline-offset: 2px;
}

.version-banner code {
  font-size: 13px;
}
</style>
