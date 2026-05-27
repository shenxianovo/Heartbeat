<script setup lang="ts">
import { computed } from 'vue'
import { useHeartbeat } from '../composables/useHeartbeat'
import { authStore } from '../stores/auth'
import ActivityTimeline from './ActivityTimeline.vue'
import StatusCards from './StatusCards.vue'
import CurrentAppPanel from './CurrentAppPanel.vue'
import TodayRanking from './TodayRanking.vue'
import WeeklyChart from './WeeklyChart.vue'

const props = defineProps<{ username: string }>()

const isOwnProfile = computed(() =>
  authStore.isAuthenticated && authStore.username.value === props.username
)

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
} = useHeartbeat(props.username)
</script>

<template>
  <div class="dashboard">
    <header class="header">
      <div class="header-left">
        <div class="logo">
          <span class="status-dot" :class="{ alive: isAlive }"></span>
          <span>{{ username }}</span>
        </div>
      </div>
      <div class="controls">
        <span class="tz-badge" :title="'数据按浏览器所在时区的日期展示，不代表设备所在时区'">{{ timezoneLabel }}</span>
        <select v-model="selectedDevice" class="ctl">
          <option v-for="d in devices" :key="d.id" :value="d.id">{{ d.name }}</option>
        </select>
        <input type="date" v-model="selectedDate" class="ctl" />
        <a v-if="isOwnProfile" href="/heartbeat/settings" class="ctl btn-link">设置</a>
        <button v-if="authStore.isAuthenticated" class="ctl btn-logout" @click="authStore.logout()">登出</button>
        <button v-else class="ctl btn-login" @click="authStore.redirectToLogin()">登录</button>
      </div>
    </header>

    <main>
      <StatusCards
        :isToday="isToday"
        :isAlive="isAlive"
        :lastSeenStr="lastSeenStr"
        :appSummaries="appSummaries"
        :totalSeconds="totalSeconds"
      />

      <div class="main-grid">
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
.dashboard {
  width: min(100%, 1400px);
  margin: 0 auto;
  padding: clamp(1rem, 3vw, 2.5rem) clamp(0.75rem, 3vw, 2.5rem);
  position: relative;
}

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

.btn-logout {
  background: transparent;
  color: var(--text-dim);
  font-size: 0.8rem;
  cursor: pointer;
  transition: color 0.2s;
}

.btn-logout:hover {
  color: var(--text);
}

.btn-login {
  background: var(--accent);
  color: #fff;
  border-color: var(--accent);
  font-size: 0.8rem;
  cursor: pointer;
}

.btn-link {
  text-decoration: none;
  color: var(--text-dim);
  font-size: 0.8rem;
  display: inline-flex;
  align-items: center;
  transition: color 0.2s;
}

.btn-link:hover {
  color: var(--text);
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

.main-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 0;
}

.col-main,
.col-aside {
  min-width: 0;
}

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

@media (min-width: 1200px) {
  .main-grid {
    grid-template-columns: 1fr 420px;
    gap: 1.5rem;
  }
}

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
