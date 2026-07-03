<script setup lang="ts">
import { computed, ref } from 'vue'
import { useHeartbeat } from '../composables/useHeartbeat'
import { authStore } from '../stores/auth'
import ActivityTimeline from './ActivityTimeline.vue'
import StatusCards from './StatusCards.vue'
import CurrentAppPanel from './CurrentAppPanel.vue'
import TodayRanking from './TodayRanking.vue'
import WeeklyChart from './WeeklyChart.vue'
import KeyboardHeatmap from './KeyboardHeatmap.vue'
import AppDetailModal from './AppDetailModal.vue'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import DatePicker from './DatePicker.vue'

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
  awaySeconds,
  maxSeconds,
  activeHours,
  weeklyAppSummaries,
  weeklyTotalSeconds,
  includeAway,
  keyFrequency,
  timezoneLabel,
  titleBreakdown,
} = useHeartbeat(props.username)

// Reka UI Select 用字符串值，selectedDevice 是 number —— 用 computed 双向桥接
const selectedDeviceStr = computed({
  get: () => String(selectedDevice.value),
  set: (v: string) => { selectedDevice.value = Number(v) },
})

// 点击排行条目 → 全局全屏应用详情弹窗（回放多轨 + 标题明细）
const selectedApp = ref<{ appId: number; appName: string; totalSeconds: number } | null>(null)
</script>

<template>
  <div class="relative z-10 mx-auto w-[min(100%,1400px)] px-[clamp(0.75rem,3vw,2.5rem)] py-[clamp(1rem,3vw,2.5rem)]">
    <header class="mb-[clamp(1.25rem,3vw,2rem)] flex flex-wrap items-center justify-between gap-x-4 gap-y-3 pr-12 max-[640px]:flex-col max-[640px]:items-stretch max-[640px]:pr-0">
      <div class="flex select-none items-center gap-3 whitespace-nowrap font-display text-[clamp(1.15rem,2.5vw,1.5rem)] font-bold tracking-tight max-[640px]:pr-12">
        <span class="status-dot" :class="{ alive: isAlive }"></span>
        <span>{{ username }}</span>
      </div>

      <div class="flex flex-wrap items-center gap-2 max-[640px]:w-full">
        <Select v-model="selectedDeviceStr">
          <SelectTrigger class="glass-control h-auto min-w-[8rem] border-glass-border px-3 py-1.5 text-sm shadow-sm max-[640px]:flex-1">
            <SelectValue placeholder="选择设备" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem v-for="d in devices" :key="d.id" :value="String(d.id)">
              {{ d.name }}
            </SelectItem>
          </SelectContent>
        </Select>

        <DatePicker v-model="selectedDate" class="max-[640px]:flex-1" />

        <span
          class="glass-control cursor-help whitespace-nowrap px-3 py-1.5 font-mono text-[0.7rem] text-muted-foreground"
          title="数据按浏览器所在时区的日期展示，不代表设备所在时区"
        >{{ timezoneLabel }}</span>

        <button
          class="glass-control cursor-pointer whitespace-nowrap px-3 py-1.5 text-[0.8rem] transition-colors"
          :class="includeAway ? 'text-primary' : 'text-muted-foreground hover:text-foreground'"
          :title="includeAway ? '统计已包含离开时间（息屏/睡眠/锁屏）' : '统计不含离开时间，点击计入'"
          @click="includeAway = !includeAway"
        >{{ includeAway ? '含离开' : '不含离开' }}</button>

        <a
          v-if="isOwnProfile"
          href="/heartbeat/settings"
          class="glass-control px-3 py-1.5 text-[0.8rem] text-muted-foreground no-underline hover:text-foreground"
        >设置</a>
        <button
          v-if="authStore.isAuthenticated"
          class="glass-control px-3 py-1.5 text-[0.8rem] text-muted-foreground hover:text-foreground"
          @click="authStore.logout()"
        >登出</button>
        <button
          v-else
          class="glass-control px-3 py-1.5 text-[0.8rem] font-medium text-primary"
          @click="authStore.redirectToLogin()"
        >登录</button>
      </div>
    </header>

    <main>
      <StatusCards
        :isToday="isToday"
        :isAlive="isAlive"
        :lastSeenStr="lastSeenStr"
        :appSummaries="appSummaries"
        :totalSeconds="totalSeconds"
        :awaySeconds="awaySeconds"
        :includeAway="includeAway"
      />

      <div class="grid grid-cols-1 gap-0 min-[900px]:grid-cols-[1fr_340px] min-[900px]:items-start min-[900px]:gap-5 min-[1200px]:grid-cols-[1fr_420px] min-[1200px]:gap-6">
        <div class="min-w-0">
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

          <KeyboardHeatmap :keyFrequency="keyFrequency" />
        </div>

        <div class="min-w-0 min-[900px]:sticky min-[900px]:top-4">
          <TodayRanking
            :appSummaries="appSummaries"
            :maxSeconds="maxSeconds"
            @select="selectedApp = $event"
          />

          <WeeklyChart
            :weeklyAppSummaries="weeklyAppSummaries"
            :weeklyTotalSeconds="weeklyTotalSeconds"
          />
        </div>
      </div>
    </main>

    <AppDetailModal
      v-if="selectedApp"
      :username="username"
      :deviceId="selectedDevice"
      :selectedDate="selectedDate"
      :app="selectedApp"
      :usageData="usageData"
      :titleBreakdown="titleBreakdown"
      @close="selectedApp = null"
    />

    <div v-if="loading" class="loading-bar"></div>
  </div>
</template>

<style scoped>
.loading-bar {
  position: fixed;
  top: 0;
  left: 0;
  width: 100%;
  height: 2px;
  background: var(--primary);
  animation: loading 1s ease-in-out infinite;
}

@keyframes loading {
  0%   { transform: scaleX(0); transform-origin: left; }
  50%  { transform: scaleX(1); transform-origin: left; }
  51%  { transform-origin: right; }
  100% { transform: scaleX(0); transform-origin: right; }
}
</style>
