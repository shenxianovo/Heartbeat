<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { authStore } from './stores/auth'
import { useHeartbeat } from './composables/useHeartbeat'
import ActivityTimeline from './components/ActivityTimeline.vue'
import StatusCards from './components/StatusCards.vue'
import CurrentAppPanel from './components/CurrentAppPanel.vue'
import TodayRanking from './components/TodayRanking.vue'
import WeeklyChart from './components/WeeklyChart.vue'

const ready = ref(false)

onMounted(() => {
  authStore.handleCallback()

  if (!authStore.isAuthenticated) {
    authStore.redirectToLogin()
    return
  }

  ready.value = true
})

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
  <div v-if="ready" class="dashboard">
    <header class="header">
      <div class="header-left">
        <div class="logo">
          <span class="status-dot" :class="{ alive: isAlive }"></span>
          <span>-QuQ-</span>
        </div>
        <span class="header-subtitle">你在视奸我，对吧！</span>
      </div>
      <div class="controls">
        <span class="tz-badge" :title="'数据按浏览器所在时区的日期展示，不代表设备所在时区'">{{ timezoneLabel }}</span>
        <select v-model="selectedDevice" class="ctl">
          <option v-for="d in devices" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
        <input type="date" v-model="selectedDate" class="ctl" />
      </div>
    </header>

    <main>
      <!-- 状态卡片 - 始终全宽 -->
      <StatusCards 
        :isToday="isToday" 
        :isAlive="isAlive" 
        :lastSeenStr="lastSeenStr" 
        :appSummaries="appSummaries" 
        :totalSeconds="totalSeconds" 
      />

      <!-- 主体双列布局区域 -->
      <div class="main-grid">
        <!-- 左列：当前使用 + 活动时间线 -->
        <div class="col-main">
          <CurrentAppPanel 
            :isToday="isToday" 
            :isAlive="isAlive" 
            :currentApp="currentApp" 
            :currentAppId="currentAppId" 
          />

          <ActivityTimeline
            :activeHours="activeHours"
            :usageData="usageData"
            :appNameMap="appNameMap"
            :selectedDate="selectedDate"
            :isToday="isToday"
          />
        </div>

        <!-- 右列：排行 + 本周使用 -->
        <div class="col-aside">
          <TodayRanking 
            :appSummaries="appSummaries" 
            :maxSeconds="maxSeconds" 
          />

          <WeeklyChart 
            :weeklyAppSummaries="weeklyAppSummaries" 
            :weeklyTotalSeconds="weeklyTotalSeconds" 
          />
        </div>
      </div>
    </main>

    <div v-if="loading" class="loading-bar"></div>
  </div>
</template>

<style scoped>
/* ===========================================
   Base — fluid width with clamp
   =========================================== */
.dashboard {
  /* Fluid: min 100%, preferred 90vw, max 1400px */
  width: min(100%, 1400px);
  margin: 0 auto;
  padding: clamp(1rem, 3vw, 2.5rem) clamp(0.75rem, 3vw, 2.5rem);
  position: relative;
}

/* ===========================================
   Header — responsive wrap
   =========================================== */
.header {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 0.75rem 1rem;
  margin-bottom: clamp(1.25rem, 3vw, 2rem);
}

.header-left {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
}

.logo {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  font-size: clamp(1.15rem, 2.5vw, 1.5rem);
  font-weight: 700;
  letter-spacing: -0.02em;
  user-select: none;
  white-space: nowrap;
}

.header-subtitle {
  font-size: 0.75rem;
  color: var(--text-dim);
}

.controls {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.ctl {
  background: var(--bg-card);
  border: 1px solid var(--border);
  color: var(--text);
  padding: 0.4rem 0.65rem;
  border-radius: 6px;
  font-size: 0.85rem;
  outline: none;
  cursor: pointer;
  transition: border-color 0.2s;
  min-width: 0;
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
  padding: 0.4rem 0.65rem;
  border-radius: 6px;
  font-size: 0.7rem;
  font-family: var(--font-mono);
  cursor: help;
  user-select: none;
  white-space: nowrap;
}

/* ===========================================
   Main Grid — single column by default
   =========================================== */
.main-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 0;
}

.col-main,
.col-aside {
  min-width: 0; /* prevent flex/grid overflow */
}

/* ===========================================
   Loading bar
   =========================================== */
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
  0%   { transform: scaleX(0); transform-origin: left; }
  50%  { transform: scaleX(1); transform-origin: left; }
  51%  { transform-origin: right; }
  100% { transform: scaleX(0); transform-origin: right; }
}

/* ===========================================
   Breakpoint: ≥900px — two columns kick in
   =========================================== */
@media (min-width: 900px) {
  .main-grid {
    grid-template-columns: 1fr 340px;
    gap: 1.25rem;
    align-items: start;
  }

  .col-aside {
    position: sticky;
    top: 1rem;
  }
}

/* ===========================================
   Breakpoint: ≥1200px — wider aside column
   =========================================== */
@media (min-width: 1200px) {
  .main-grid {
    grid-template-columns: 1fr 420px;
    gap: 1.5rem;
  }
}

/* ===========================================
   Breakpoint: ≤640px — mobile compact
   =========================================== */
@media (max-width: 640px) {
  .header {
    flex-direction: column;
    align-items: flex-start;
  }

  .controls {
    width: 100%;
  }

  .ctl {
    flex: 1;
    text-align: center;
  }
}
</style>