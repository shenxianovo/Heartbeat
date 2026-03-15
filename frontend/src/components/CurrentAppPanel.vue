<script setup lang="ts">
import { getIconUrl } from '../api/index'
import { getAppLabel } from '../appLabels'

defineProps<{
  isToday: boolean
  isAlive: boolean
  currentApp: string | null
  currentAppId: number | null
}>()
</script>

<template>
  <section class="panel current-app-panel" v-if="isToday">
    <h2>当前使用</h2>
    <div class="current-app" v-if="isAlive && currentApp">
      <span class="current-dot alive"></span>
      <img
        v-if="currentAppId"
        :src="getIconUrl(currentAppId)"
        class="current-icon"
        @error="($event.target as HTMLImageElement).style.display = 'none'"
      />
      <div class="current-info">
        <span class="current-name">{{ currentApp }}</span>
        <span class="current-desc" v-if="getAppLabel(currentApp)">{{ getAppLabel(currentApp) }}</span>
      </div>
    </div>
    <div class="current-app offline" v-else-if="!isAlive">
      <span class="current-dot"></span>
      <span class="current-name dim">设备离线</span>
    </div>
    <div class="current-app" v-else>
      <span class="current-dot alive"></span>
      <span class="current-name dim">无前台应用</span>
    </div>
  </section>
</template>

<style scoped>
.panel {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 1.25rem;
  margin-bottom: 1.5rem;
}
.panel h2 {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-dim);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  margin-bottom: 1rem;
}

.current-app {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.25rem 0;
}

@keyframes pulse {
  0%, 100% { opacity: 1; box-shadow: 0 0 8px var(--alive); }
  50% { opacity: 0.4; box-shadow: 0 0 2px var(--alive); }
}

.current-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #444;
  flex-shrink: 0;
}

.current-dot.alive {
  background: var(--alive);
  box-shadow: 0 0 8px var(--alive);
  animation: pulse 2s ease-in-out infinite;
}

.current-icon {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  object-fit: contain;
  flex-shrink: 0;
}

.current-name {
  font-size: 1.1rem;
  font-weight: 600;
}

.current-name.dim {
  color: var(--text-dim);
  font-weight: 400;
}

.current-info {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
}

.current-desc {
  font-size: 0.8rem;
  color: var(--text-dim);
}
</style>