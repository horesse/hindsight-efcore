<script setup lang="ts">
import { computed, ref } from 'vue'
import { useData } from 'vitepress'
import { currentVersion, switchTo, useVersions, versionHome, versionLabel } from './versions'

/** `screen`: render as a plain list for the mobile navigation screen instead of a dropdown. */
const props = defineProps<{ screen?: boolean }>()

const { site } = useData()
const manifest = useVersions()
const open = ref(false)

const options = computed(() => {
  const m = manifest.value
  if (!m) return []
  const items = m.stable.map(v => ({ version: v, text: versionLabel(v), note: v === m.latest ? 'latest' : '' }))
  if (m.nightly) items.push({ version: 'nightly', text: 'nightly', note: 'unreleased' })
  return items
})

function select(version: string) {
  open.value = false
  if (version !== currentVersion) void switchTo(version, site.value.base)
}

function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (!next || !(event.currentTarget as HTMLElement).contains(next)) open.value = false
}
</script>

<template>
  <div
    v-if="options.length"
    class="version-switcher"
    :class="{ screen: props.screen, open }"
    @focusout="onFocusOut"
    @keydown.escape="open = false"
  >
    <template v-if="props.screen">
      <p class="title">Documentation version</p>
      <a
        v-for="o in options"
        :key="o.version"
        class="item"
        :class="{ active: o.version === currentVersion }"
        :href="versionHome(o.version)"
        :aria-current="o.version === currentVersion ? 'page' : undefined"
        @click.prevent="select(o.version)"
      >
        {{ o.text }}<span v-if="o.note" class="note">{{ o.note }}</span>
      </a>
    </template>
    <template v-else>
      <button
        type="button"
        class="trigger"
        aria-haspopup="true"
        :aria-expanded="open"
        :aria-label="`Documentation version: ${versionLabel(currentVersion)}`"
        @click="open = !open"
      >
        {{ versionLabel(currentVersion) }}
        <span class="chevron" aria-hidden="true" />
      </button>
      <ul class="menu" role="menu">
        <li v-for="o in options" :key="o.version" role="none">
          <a
            role="menuitem"
            class="item"
            :class="{ active: o.version === currentVersion }"
            :href="versionHome(o.version)"
            :aria-current="o.version === currentVersion ? 'page' : undefined"
            @click.prevent="select(o.version)"
          >
            {{ o.text }}<span v-if="o.note" class="note">{{ o.note }}</span>
          </a>
        </li>
      </ul>
    </template>
  </div>
</template>

<style scoped>
.version-switcher:not(.screen) {
  position: relative;
  display: none;
  align-items: center;
  height: var(--vp-nav-height);
  margin-left: 12px;
}

@media (min-width: 768px) {
  .version-switcher:not(.screen) {
    display: flex;
  }
}

.trigger {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 10px;
  height: 32px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 8px;
  font-size: 13px;
  font-weight: 500;
  color: var(--vp-c-text-1);
  transition: border-color 0.25s;
}

.trigger:hover,
.open .trigger {
  border-color: var(--vp-c-brand-1);
}

.chevron {
  width: 6px;
  height: 6px;
  border-right: 1.5px solid currentColor;
  border-bottom: 1.5px solid currentColor;
  transform: translateY(-2px) rotate(45deg);
}

.menu {
  position: absolute;
  top: calc(var(--vp-nav-height) / 2 + 20px);
  right: 0;
  min-width: 160px;
  margin: 0;
  padding: 8px;
  list-style: none;
  border: 1px solid var(--vp-c-divider);
  border-radius: 12px;
  background-color: var(--vp-c-bg-elv);
  box-shadow: var(--vp-shadow-3);
  opacity: 0;
  visibility: hidden;
  transition: opacity 0.25s, visibility 0.25s;
}

.version-switcher:not(.screen):hover .menu,
.open .menu {
  opacity: 1;
  visibility: visible;
}

.item {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  padding: 0 12px;
  border-radius: 6px;
  line-height: 32px;
  font-size: 14px;
  font-weight: 500;
  color: var(--vp-c-text-1);
  white-space: nowrap;
  transition: background-color 0.25s, color 0.25s;
}

.item:hover {
  background-color: var(--vp-c-default-soft);
  color: var(--vp-c-brand-1);
}

.item.active {
  color: var(--vp-c-brand-1);
}

.note {
  font-size: 12px;
  font-weight: 400;
  color: var(--vp-c-text-3);
}

.screen {
  margin-top: 24px;
  padding-top: 16px;
  border-top: 1px solid var(--vp-c-divider);
}

.screen .title {
  margin: 0 0 8px;
  font-size: 14px;
  font-weight: 500;
  color: var(--vp-c-text-2);
}

.screen .item {
  padding: 0;
}
</style>
