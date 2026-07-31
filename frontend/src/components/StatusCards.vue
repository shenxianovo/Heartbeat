<script setup lang="ts">
import { computed } from 'vue'
import { getIconUrl } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'
import { Card } from '@/components/ui/card'

const props = defineProps<{
  username: string
  isToday: boolean
  isAlive: boolean
  lastSeenStr: string
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  totalSeconds: number
  awaySeconds: number
  /** 在线并集:滤掉 away 后跨设备去重的墙钟时长,答"我今天在多久" */
  onlineSeconds: number
  /** per-device 屏幕占用求和,允许超 24h,答"屏幕被谁占用" */
  perDeviceSeconds: { deviceId: number; usageSeconds: number; awaySeconds: number }[]
  hasConcurrentUse: boolean
  isAllDevices: boolean
  includeAway: boolean
}>()

// 主数字用并集(人只有一个),求和降为副数字。单设备时两者相等,只显示一个。
const showSumAsSecondary = computed(() =>
  props.isAllDevices && props.hasConcurrentUse && props.totalSeconds > props.onlineSeconds
)
</script>

<template>
  <section class="mb-6 grid grid-cols-[repeat(auto-fit,minmax(220px,1fr))] gap-4 max-[640px]:grid-cols-1">
    <!-- 死了吗 -->
    <Card class="gap-1.5 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
      <div class="flex flex-col gap-1.5 px-5">
        <span class="text-xs uppercase tracking-[0.06em] text-muted-foreground">死了吗</span>
        <span
          class="text-[1.75rem] font-bold"
          :class="isToday ? (isAlive ? 'text-alive' : 'text-dead') : 'text-muted-foreground'"
        >
          {{ isToday ? (isAlive ? '还活着' : '似了喵') : '--' }}
        </span>
        <span class="text-[0.8rem] text-muted-foreground" v-if="lastSeenStr && isToday">
          最后活跃 {{ lastSeenStr }}
        </span>
      </div>
    </Card>

    <!-- 本次存活 -->
    <Card class="gap-1.5 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
      <div class="flex flex-col gap-1.5 px-5">
        <span class="text-xs uppercase tracking-[0.06em] text-muted-foreground">本次存活</span>
        <!-- 主数字 = 在线并集:两台机同时开着不算两份人生 -->
        <span
          class="font-mono text-[1.75rem] font-bold text-foreground"
          :title="showSumAsSecondary ? '跨设备去重后的实际在线时长' : undefined"
        >{{ formatDuration(onlineSeconds) }}</span>
        <span class="text-[0.8rem] text-muted-foreground">
          {{ appSummaries.length }} 个应用<!--
          --><template v-if="showSumAsSecondary"> · <span title="各设备时长求和,并发使用会超过实际在线时长">屏幕占用 {{ formatDuration(totalSeconds) }}</span></template><!--
          --><template v-if="awaySeconds > 0"> · <span title="设备开着但人不在(息屏/睡眠/锁屏),各设备求和">{{ includeAway ? '含' : '另有' }}空转 {{ formatDuration(awaySeconds) }}</span></template>
        </span>
      </div>
    </Card>

    <!-- 今日最爱 -->
    <Card class="gap-1.5 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
      <div class="flex flex-col gap-1.5 px-5">
        <span class="text-xs uppercase tracking-[0.06em] text-muted-foreground">今日最爱</span>
        <span v-if="appSummaries[0]" class="flex items-center gap-2 text-[1.25rem] font-bold text-foreground">
          <img
            :src="getIconUrl(username, appSummaries[0].appId)"
            class="h-6 w-6 rounded object-contain"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <span class="truncate">{{ appSummaries[0].appName }}</span>
        </span>
        <span v-else class="text-[1.25rem] font-bold text-muted-foreground">--</span>
        <span class="text-[0.8rem] text-muted-foreground" v-if="appSummaries[0]">
          沉迷时长 {{ formatDuration(appSummaries[0].totalSeconds) }}
        </span>
      </div>
    </Card>
  </section>
</template>
