<script setup lang="ts">
import { useHeartbeat } from './composables/useHeartbeat'
import ActivityTimeline from './components/ActivityTimeline.vue'
import StatusCards from './components/StatusCards.vue'
import CurrentAppPanel from './components/CurrentAppPanel.vue'
import TodayRanking from './components/TodayRanking.vue'
import WeeklyChart from './components/WeeklyChart.vue'

const {
  devices,
  selectedDevice,
  selectedDate,
  usageData,
  appNameMap,
  loading,
  isToday,
  isAlive,
  currentApp,
  currentAppId,
  lastSeenStr,
  appSummaries,
  totalSeconds,
  maxSeconds,
  activeHours,
  weeklyAppSummaries,
  weeklyTotalSeconds,
  timezoneLabel,
} = useHeartbeat()

</script>

<template>
  <div class="dashboard">
    <header class="header">
      <div class="logo">
        <span class="status-dot" :class="{ alive: isAlive }"></span>
        <span>-QuQ-</span>
      </div>
      <span class="card-label">你在视奸我，对吧！</span>
      <div class="controls">
        <span class="tz-badge" :title="'数据按浏览器所在时区的日期展示，不代表设备所在时区'">{{ timezoneLabel }}</span>
        <select v-model="selectedDevice" class="ctl">
          <option v-for="d in devices" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
        <input type="date" v-model="selectedDate" class="ctl" />
      </div>
    </header>

    <main>
      <!-- 状态卡片 -->
      <StatusCards 
        :isToday="isToday" 
        :isAlive="isAlive" 
        :lastSeenStr="lastSeenStr" 
        :appSummaries="appSummaries" 
        :totalSeconds="totalSeconds" 
      />

      <!-- 当前使用 -->
      <CurrentAppPanel 
        :isToday="isToday" 
        :isAlive="isAlive" 
        :currentApp="currentApp" 
        :currentAppId="currentAppId" 
      />

      <!-- 活动时间线 -->
      <ActivityTimeline
        :activeHours="activeHours"
        :usageData="usageData"
        :appNameMap="appNameMap"
        :selectedDate="selectedDate"
        :isToday="isToday"
      />

      <!-- 今日应用时长排行 -->
      <TodayRanking 
        :appSummaries="appSummaries" 
        :maxSeconds="maxSeconds" 
      />

      <!-- 本周应用使用 -->
      <WeeklyChart 
        :weeklyAppSummaries="weeklyAppSummaries" 
        :weeklyTotalSeconds="weeklyTotalSeconds" 
      />
    </main>

    <div v-if="loading" class="loading-bar"></div>
  </div>
</template>

<style scoped>
.dashboard {
  max-width: 860px;
  margin: 0 auto;
  padding: 2rem 1.5rem;
  position: relative;
}

/* Header */
.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
}

.logo {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  font-size: 1.5rem;
  font-weight: 700;
  letter-spacing: -0.02em;
  user-select: none;
}

.controls {
  display: flex;
  gap: 0.75rem;
}

.ctl {
  background: var(--bg-card);
  border: 1px solid var(--border);
  color: var(--text);
  padding: 0.5rem 0.75rem;
  border-radius: 6px;
  font-size: 0.875rem;
  outline: none;
  cursor: pointer;
  transition: border-color 0.2s;
}

.ctl:focus {
  border-color: var(--accent);
}

.tz-badge {
  display: inline-flex;
  align-items: center;
  background: var(--bg-card);
  border: 1px solid var(--border);
  color: var(--text-dim);
  padding: 0.5rem 0.75rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-family: var(--font-mono);
  cursor: help;
  user-select: none;
}

/* Loading bar */
.loading-bar {
  position: fixed;
  top: 0;
  left: 0;
  width: 100%;
  height: 2px;
  background: var(--accent);
  animation: loading 1s ease-in-out infinite;
}

@keyframes loading {
  0% { transform: scaleX(0); transform-origin: left; }
  50% { transform: scaleX(1); transform-origin: left; }
  51% { transform-origin: right; }
  100% { transform: scaleX(0); transform-origin: right; }
}

/* Responsive */
@media (max-width: 640px) {
  .header {
    flex-direction: column;
    gap: 1rem;
    align-items: flex-start;
  }

  .dashboard {
    padding: 1.5rem 1rem;
  }
}
</style>