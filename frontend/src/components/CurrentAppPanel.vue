<script setup lang="ts">
import { computed } from 'vue'
import { getIconUrl } from '../api/index'
import { getAppLabel, isAwayApp } from '../appLabels'
import type { DevicePresence } from '../composables/useDeviceStatus'
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
}>()

// 多台设备时逐行展示：双机并发时"当前应用"本来就不是一个值,不合成。
const showPerDevice = computed(() => props.isAllDevices && props.presences.length > 1)
</script>

<template>
  <Card v-if="isToday" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">当前使用</h2>

      <!-- 聚合视图多设备：per-device 行,每台各说自己的前台应用 -->
      <template v-if="showPerDevice">
        <div
          v-for="p in presences"
          :key="p.deviceId"
          class="flex items-center gap-3 border-b border-border/40 py-1.5 last:border-0"
        >
          <span class="status-dot" :class="{ alive: p.isOnline }"></span>
          <img
            v-if="p.isOnline && p.currentAppId && !isAwayApp(p.currentAppKey, p.currentApp)"
            :src="getIconUrl(username, p.currentAppId)"
            class="h-6 w-6 shrink-0 object-contain"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <div class="flex min-w-0 flex-col gap-0.5">
            <span
              class="truncate text-[1rem]"
              :class="p.isOnline && p.currentApp && !isAwayApp(p.currentAppKey, p.currentApp)
                ? 'font-semibold'
                : 'font-normal text-muted-foreground'"
            >
              {{ !p.isOnline ? '离线' : isAwayApp(p.currentAppKey, p.currentApp) ? '离开中' : (p.currentApp ?? '无前台应用') }}
            </span>
            <span v-if="p.isOnline && p.currentApp && getAppLabel(p.currentAppKey ?? p.currentApp)" class="text-[0.7rem] text-muted-foreground">
              {{ getAppLabel(p.currentAppKey ?? p.currentApp) }}
            </span>
            <span class="truncate text-[0.75rem] text-muted-foreground">
              {{ p.deviceName }}<template v-if="!p.isOnline && p.lastSeenStr"> · 最后活跃 {{ p.lastSeenStr }}</template>
            </span>
          </div>
        </div>
      </template>

      <!-- 在线但人离开（心跳照实上报 __away__，ADR-021） -->
      <div v-else-if="isAlive && isAwayApp(currentAppKey, currentApp)" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <span class="text-[1.1rem] font-normal text-muted-foreground">离开中</span>
      </div>

      <!-- 在线 + 有前台应用 -->
      <div v-else-if="isAlive && currentApp" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <img
          v-if="currentAppId"
          :src="getIconUrl(username, currentAppId)"
          class="h-7 w-7 shrink-0 object-contain"
          @error="($event.target as HTMLImageElement).style.display = 'none'"
        />
        <div class="flex flex-col gap-0.5">
          <span class="text-[1.1rem] font-semibold">{{ currentApp }}</span>
          <span v-if="getAppLabel(currentAppKey ?? currentApp)" class="text-[0.8rem] text-muted-foreground">
            {{ getAppLabel(currentAppKey ?? currentApp) }}
          </span>
        </div>
      </div>

      <!-- 离线 -->
      <div v-else-if="!isAlive" class="flex items-center gap-3 py-1">
        <span class="status-dot"></span>
        <span class="text-[1.1rem] font-normal text-muted-foreground">设备离线</span>
      </div>

      <!-- 在线但无前台应用 -->
      <div v-else class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <span class="text-[1.1rem] font-normal text-muted-foreground">无前台应用</span>
      </div>
    </div>
  </Card>
</template>
