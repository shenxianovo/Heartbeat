<script setup lang="ts">
import { getIconUrl } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'

defineProps<{
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  maxSeconds: number
}>()
</script>

<template>
  <section class="panel">
    <h2>今日应用时长排行</h2>
    <div v-if="appSummaries.length" class="ranking">
      <div v-for="(app, i) in appSummaries" :key="app.appName" class="rank-row">
        <div class="rank-meta">
          <span class="rank-i">{{ i + 1 }}</span>
          <img
            :src="getIconUrl(app.appId)"
            class="rank-icon"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <span class="rank-name">{{ app.appName }}</span>
          <span class="rank-dur">{{ formatDuration(app.totalSeconds) }}</span>
        </div>
        <div class="bar-bg">
          <div
            class="bar"
            :style="{ width: `${(app.totalSeconds / maxSeconds) * 100}%` }"
          ></div>
        </div>
      </div>
    </div>
    <div v-else class="empty">暂无数据</div>
  </section>
</template>

<style scoped>
/* panel is global now or wait, the panel styles are in App.vue and unscoped maybe? */
/* Actually App.vue style is scoped! I'll need to move relevant styles here */
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
  margin-top: 0;
}
.ranking { 
  display: flex; 
  flex-direction: column; 
  gap: 0.75rem; 
  max-height: 280px; /* ~7 items */
  overflow-y: auto;
  padding-right: 4px;
}
.ranking::-webkit-scrollbar {
  width: 4px;
}
.ranking::-webkit-scrollbar-track {
  background: transparent;
}
.ranking::-webkit-scrollbar-thumb {
  background-color: var(--border);
  border-radius: 2px;
}
.rank-row { display: flex; flex-direction: column; gap: 0.25rem; }
.rank-meta { display: flex; align-items: center; gap: 0.5rem; font-size: 0.85rem; }
.rank-i { width: 1.5rem; color: var(--text-dim); font-size: 0.75rem; font-weight: 600; text-align: center; }
.rank-icon { width: 18px; height: 18px; border-radius: 4px; object-fit: contain; }
.rank-name { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.rank-dur { font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace; color: var(--text-dim); font-size: 0.8rem; }
.bar-bg { height: 4px; background: rgba(255, 255, 255, 0.05); border-radius: 2px; overflow: hidden; margin-left: 2rem; }
.bar { height: 100%; background: var(--accent); border-radius: 2px; }
.empty { text-align: center; padding: 2rem; color: var(--text-dim); font-size: 0.9rem; }
</style>