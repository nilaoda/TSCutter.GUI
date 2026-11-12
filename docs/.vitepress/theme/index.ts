import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import Screenshot from './Screenshot.vue'
import SiteNotice from './SiteNotice.vue'
import './custom.css'

export default {
  extends: DefaultTheme,
  Layout: () => h(DefaultTheme.Layout, null, { 'doc-footer-before': () => h(SiteNotice) }),
  enhanceApp({ app }) {
    app.component('Screenshot', Screenshot)
  }
} satisfies Theme
