<script setup lang="ts">
import { getIconUrl } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'

defineProps<{
  isToday: boolean
  isAlive: boolean
  lastSeenStr: string
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  totalSeconds: number
}>()
</script>

<template>
  <section class="cards">
    <div class="card">
      <span class="card-label">死了吗</span>
      <span
        class="card-value status"
        :class="isToday ? (isAlive ? 'alive' : 'dead') : 'off'"
      >
        {{ isToday ? (isAlive ? '还活着' : '似了喵') : '--' }}
      </span>
      <span class="card-sub" v-if="lastSeenStr && isToday">
        最后活跃 {{ lastSeenStr }}
      </span>
    </div>
    <div class="card">
      <span class="card-label">本次存活</span>
      <span class="card-value accent" style="color: var(--text);">{{ formatDuration(totalSeconds) }}</span>
      <span class="card-sub">{{ appSummaries.length }} 个应用</span>
    </div>
    <div class="card">
      <span class="card-label">今日最爱</span>
      <span class="card-value accent top-app" v-if="appSummaries[0]" style="color: var(--text);">
        <img
          :src="getIconUrl(appSummaries[0].appId)"
          class="top-app-icon"
          @error="($event.target as HTMLImageElement).style.display = 'none'"
        />
        {{ appSummaries[0].appName }}
      </span>
      <span class="card-value accent top-app" v-else>--</span>
      <span class="card-sub" v-if="appSummaries[0]">
        沉迷时长 {{ formatDuration(appSummaries[0].totalSeconds) }}
      </span>
    </div>
  </section>
</template>

<style scoped>
.cards {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 1rem;
  margin-bottom: 1.5rem;
}

.card {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 1.25rem;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.card-label {
  font-size: 0.75rem;
  color: var(--text-dim);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.card-value {
  font-size: 1.75rem;
  font-weight: 700;
  font-family: 'Cascadia Code', 'Microsoft YaHei', 'SF Mono', 'Consolas', monospace;
}

.card-value.accent {
  color: var(--accent);
}

.card-value.top-app {
  font-size: 1.25rem;
  font-family: inherit;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.top-app-icon {
  width: 24px;
  height: 24px;
  border-radius: 4px;
  object-fit: contain;
}

.card-value.status.alive {
  color: var(--alive);
}

.card-value.status.dead {
  color: var(--dead);
}

.card-value.status.off {
  color: var(--text-dim);
}

.card-sub {
  font-size: 0.8rem;
  color: var(--text-dim);
}
</style>