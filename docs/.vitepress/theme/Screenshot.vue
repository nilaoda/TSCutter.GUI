<script setup lang="ts">
import { computed } from 'vue'
import { useData, withBase } from 'vitepress'

// 按当前页面语言与浅色/深色模式显示对应的截图：screenshots/<语言>/<名称>-light.webp 与 -dark.webp。
const props = defineProps<{ name: string; alt: string; width?: number | string }>()
const { lang } = useData()
const folder = computed(() => (lang.value === 'zh-TW' ? 'zh-tw' : lang.value === 'en-US' ? 'en' : 'zh-cn'))
const source = (variant: 'light' | 'dark') => withBase(`/screenshots/${folder.value}/${props.name}-${variant}.webp`)
</script>

<template>
  <figure class="screenshot">
    <img class="screenshot-light" :src="source('light')" :alt="alt" :width="width ?? 1280" :height="width ? undefined : 800" loading="lazy" />
    <img class="screenshot-dark" :src="source('dark')" :alt="alt" :width="width ?? 1280" :height="width ? undefined : 800" loading="lazy" />
  </figure>
</template>
