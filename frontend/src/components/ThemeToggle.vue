<script setup lang="ts">
import { Moon, Sun } from 'lucide-vue-next'
import { useTheme } from '@/composables/useTheme'

const { isDark, toggle } = useTheme()
</script>

<template>
  <button
    class="glass-control theme-toggle"
    :title="isDark ? '切换到浅色' : '切换到深色'"
    @click="toggle"
  >
    <component :is="isDark ? Sun : Moon" :size="18" />
  </button>
</template>

<style scoped>
.theme-toggle {
  position: fixed;
  top: 1rem;
  right: 1rem;
  z-index: 50;
  justify-content: center;
  width: 2.5rem;
  height: 2.5rem;
  padding: 0;
}
</style>
