import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import { h } from 'vue'
import VersionBanner from './VersionBanner.vue'
import VersionSwitcher from './VersionSwitcher.vue'
import './custom.css'

// The default theme, plus the two pieces versioned docs need: a version switcher in the navigation
// bar and a banner on pages that are not the latest release. Both use public layout slots only.
export default {
  extends: DefaultTheme,
  Layout: () =>
    h(DefaultTheme.Layout, null, {
      'nav-bar-content-after': () => h(VersionSwitcher),
      'nav-screen-content-after': () => h(VersionSwitcher, { screen: true }),
      'doc-before': () => h(VersionBanner),
      'home-hero-before': () => h(VersionBanner, { home: true }),
    }),
} satisfies Theme
