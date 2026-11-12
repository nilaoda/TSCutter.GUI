<script setup lang="ts">
import { useData } from 'vitepress'

// 文档页没有默认页脚，在正文下方显示 AI 生成说明。
const { theme } = useData()
</script>

<template>
  <p v-if="theme.notice" class="site-notice">{{ theme.notice }}</p>
</template>
