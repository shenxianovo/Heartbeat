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
.ranking { 
  display: flex; 
  flex-direction: column; 
  gap: 0.75rem; 
  max-height: 200px; /* ~7 items */
  overflow-y: auto;
  padding-right: 4px;
}
.rank-row { display: flex; flex-direction: column; gap: 0.25rem; }
.rank-meta { display: flex; align-items: center; gap: 0.5rem; font-size: 0.85rem; }
.rank-i { width: 1.5rem; color: var(--text-dim); font-size: 0.75rem; font-weight: 600; text-align: center; }
.rank-icon { width: 18px; height: 18px; border-radius: 4px; object-fit: contain; }
.rank-name { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.rank-dur { font-family: var(--font-mono); color: var(--text-dim); font-size: 0.8rem; }
.bar-bg { height: 4px; background: rgba(255, 255, 255, 0.05); border-radius: 2px; overflow: hidden; margin-left: 2rem; }
.bar { height: 100%; background: var(--accent); border-radius: 2px; }
</style>