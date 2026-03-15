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
        <span class="dot" :class="{ alive: isAlive }"></span>
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

.dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #444;
  transition: background 0.3s;
}

.dot.alive {
  background: var(--alive);
  box-shadow: 0 0 8px var(--alive);
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% { opacity: 1; box-shadow: 0 0 8px var(--alive); }
  50% { opacity: 0.4; box-shadow: 0 0 2px var(--alive); }
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
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
  cursor: help;
  user-select: none;
}

/* Cards */
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

/* Panel */
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

/* Current App Panel */
.current-app {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.25rem 0;
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

/* Timeline */
.timeline {
  display: flex;
  gap: 2px;
  height: 28px;
}

.tl-block {
  flex: 1;
  background: #1f1f1f;
  border-radius: 3px;
  transition: background 0.3s;
}

.tl-block.active {
  background: var(--accent);
}

.tl-labels {
  display: flex;
  justify-content: space-between;
  margin-top: 6px;
  font-size: 0.65rem;
  color: var(--text-dim);
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

/* Ranking */
.ranking {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.rank-meta {
  display: flex;
  align-items: center;
  margin-bottom: 0.3rem;
}

.rank-i {
  width: 1.5rem;
  color: var(--text-dim);
  font-size: 0.8rem;
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.rank-icon {
  width: 20px;
  height: 20px;
  margin-right: 0.5rem;
  border-radius: 4px;
  object-fit: contain;
  flex-shrink: 0;
}

.rank-name {
  flex: 1;
  font-size: 0.9rem;
}

.rank-dur {
  font-size: 0.8rem;
  color: var(--text-dim);
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.bar-bg {
  height: 5px;
  background: #1f1f1f;
  border-radius: 3px;
  overflow: hidden;
}

.bar {
  height: 100%;
  background: linear-gradient(90deg, var(--accent), var(--accent-sub));
  border-radius: 3px;
  transition: width 0.5s ease;
}

.empty {
  text-align: center;
  color: var(--text-dim);
  padding: 2rem;
  font-size: 0.9rem;
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

  .cards {
    grid-template-columns: 1fr;
  }

  .dashboard {
    padding: 1.5rem 1rem;
  }

  .weekly-chart {
    flex-direction: column;
  }
}

/* Ranking overflow */
.ranking-overflow {
  max-height: 210px;
  overflow-y: auto;
  margin-top: 0.25rem;
  padding-top: 0.5rem;
  border-top: 1px dashed var(--border);
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.ranking-overflow::-webkit-scrollbar,
.donut-legend::-webkit-scrollbar {
  width: 4px;
}

.ranking-overflow::-webkit-scrollbar-track,
.donut-legend::-webkit-scrollbar-track {
  background: transparent;
}

.ranking-overflow::-webkit-scrollbar-thumb,
.donut-legend::-webkit-scrollbar-thumb {
  background: var(--border);
  border-radius: 2px;
}

/* Weekly donut chart */
.weekly-chart {
  display: flex;
  gap: 2rem;
  align-items: center;
}

.donut-wrapper {
  position: relative;
  width: 200px;
  height: 200px;
  flex-shrink: 0;
}

.donut-svg {
  width: 100%;
  height: 100%;
}

.donut-segment {
  cursor: pointer;
  transition: stroke-width 0.2s ease;
}

.donut-center {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  pointer-events: none;
  gap: 0.15rem;
}

.donut-center-icon {
  width: 24px;
  height: 24px;
  border-radius: 4px;
  object-fit: contain;
  margin-bottom: 0.15rem;
}

.donut-app {
  font-size: 0.8rem;
  font-weight: 600;
  text-align: center;
  max-width: 80px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.donut-dur {
  font-size: 0.75rem;
  color: var(--text-dim);
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.donut-pct {
  font-size: 0.7rem;
  color: var(--accent);
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.donut-legend {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  max-height: 200px;
  overflow-y: auto;
}

.legend-item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.85rem;
  cursor: pointer;
  padding: 0.25rem 0.5rem;
  border-radius: 4px;
  transition: opacity 0.2s, background 0.2s;
}

.legend-item:hover {
  background: rgba(255, 255, 255, 0.04);
}

.legend-item.dimmed {
  opacity: 0.35;
}

.legend-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}

.legend-icon {
  width: 18px;
  height: 18px;
  border-radius: 3px;
  object-fit: contain;
  flex-shrink: 0;
}

.legend-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.legend-dur {
  color: var(--text-dim);
  font-size: 0.75rem;
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
}

.legend-pct {
  color: var(--text-dim);
  font-size: 0.75rem;
  font-family: 'SF Mono', 'Cascadia Code', 'Consolas', monospace;
  min-width: 3rem;
  text-align: right;
}
</style>
