<script setup lang="ts">
import { computed } from 'vue'
import type { ManagedSubjectStatus } from '../api/index'
import { getAppLabel, isAwayApp } from '../appLabels'
import type { DevicePresence } from '../composables/useDeviceStatus'
import AppIcon from './AppIcon.vue'
import { Card } from '@/components/ui/card'

const props = defineProps<{
  username: string
  isToday: boolean
  isAlive: boolean
  currentApp: string | null
  currentAppId: number | null
  currentAppKey: string | null
  presences: DevicePresence[]
  isAllDevices: boolean
  managedSubjects?: ManagedSubjectStatus[]
}>()

const onlinePresences = computed(() => props.presences.filter(p => p.isOnline))
const singleDeviceName = computed(() => onlinePresences.value[0]?.deviceName ?? '')

// 多台在线设备时逐行展示：双机并发时"当前应用"本来就不是一个值,不合成。
const showPerDevice = computed(() => props.isAllDevices && onlinePresences.value.length > 1)
const accountSubjects = computed(() => props.managedSubjects ?? [])
// “当前使用”只承载事实状态。无状态的已登录/连接/异常状态归登录管理；未登录只留入口。
const visibleAccountSubjects = computed(() => accountSubjects.value.filter(subject =>
  Boolean(subject.currentActivity?.title || subject.authorization)
))

function accountState(subject: ManagedSubjectStatus): string {
  if (subject.currentActivity?.title) return subject.currentActivity.title
  return '未登录'
}
</script>

<template>
  <Card v-if="isToday && ((isAlive && onlinePresences.length > 0) || visibleAccountSubjects.length > 0)" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">当前使用</h2>

      <!-- 聚合视图多设备：per-device 行,每台各说自己的前台应用 -->
      <template v-if="isAlive && showPerDevice">
        <div
          v-for="p in onlinePresences"
          :key="p.deviceId"
          class="flex items-center gap-3 border-b border-border/40 py-1.5 last:border-0"
        >
          <span class="status-dot" :class="{ alive: p.isOnline }"></span>
          <AppIcon
            v-if="p.isOnline && p.currentAppId && !isAwayApp(p.currentAppKey, p.currentApp)"
            :username="username"
            :app-id="p.currentAppId"
            class="h-6 w-6 shrink-0 object-contain"
          />
          <div class="flex min-w-0 flex-col gap-0.5">
            <span
              class="truncate text-[1rem]"
              :class="p.isOnline && p.currentApp && !isAwayApp(p.currentAppKey, p.currentApp)
                ? 'font-semibold'
                : 'font-normal text-muted-foreground'"
            >
              {{ isAwayApp(p.currentAppKey, p.currentApp) ? '离开中' : (p.currentApp ?? '无前台应用') }}
            </span>
            <span v-if="p.isOnline && p.currentApp && getAppLabel(p.currentAppKey ?? p.currentApp)" class="text-[0.7rem] text-muted-foreground">
              {{ getAppLabel(p.currentAppKey ?? p.currentApp) }}
            </span>
            <span class="truncate text-[0.75rem] text-muted-foreground">
              {{ p.deviceName }}
            </span>
          </div>
        </div>
      </template>

      <!-- 在线但人离开（心跳照实上报 __away__，ADR-021） -->
      <div v-else-if="isAlive && isAwayApp(currentAppKey, currentApp)" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <div class="flex min-w-0 flex-col gap-0.5">
          <span class="text-[1.1rem] font-normal text-muted-foreground">离开中</span>
          <span class="truncate text-[0.75rem] text-muted-foreground">{{ singleDeviceName }}</span>
        </div>
      </div>

      <!-- 在线 + 有前台应用 -->
      <div v-else-if="isAlive && currentApp" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <AppIcon
          v-if="currentAppId"
          :username="username"
          :app-id="currentAppId"
          class="h-7 w-7 shrink-0 object-contain"
        />
        <div class="flex flex-col gap-0.5">
          <span class="text-[1.1rem] font-semibold">{{ currentApp }}</span>
          <span v-if="getAppLabel(currentAppKey ?? currentApp)" class="text-[0.8rem] text-muted-foreground">
            {{ getAppLabel(currentAppKey ?? currentApp) }}
          </span>
          <span class="truncate text-[0.75rem] text-muted-foreground">{{ singleDeviceName }}</span>
        </div>
      </div>

      <!-- 在线但无前台应用 -->
      <div v-else-if="isAlive" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <div class="flex min-w-0 flex-col gap-0.5">
          <span class="text-[1.1rem] font-normal text-muted-foreground">无前台应用</span>
          <span class="truncate text-[0.75rem] text-muted-foreground">{{ singleDeviceName }}</span>
        </div>
      </div>

      <div
        v-for="subject in visibleAccountSubjects"
        :key="subject.subjectId"
        class="border-t border-border/40 pt-3"
      >
        <div class="flex items-center gap-3">
          <span class="status-dot" :class="{ alive: Boolean(subject.currentActivity?.title) }"></span>
          <div class="min-w-0 flex-1">
            <div class="truncate text-[1rem] font-semibold">{{ accountState(subject) }}</div>
            <div class="truncate text-[0.75rem] text-muted-foreground">{{ subject.subjectName }}</div>
          </div>
          <RouterLink
            v-if="subject.authorization"
            to="/settings/logins"
            class="glass-control shrink-0 px-3 py-1.5 text-[0.8rem] font-medium text-primary"
          >去设置</RouterLink>
        </div>
      </div>
    </div>
  </Card>
</template>
