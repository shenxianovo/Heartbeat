<script setup lang="ts">
import { getIconUrl } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'
import { Card } from '@/components/ui/card'

defineProps<{
  username: string
  isToday: boolean
  isAlive: boolean
  lastSeenStr: string
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  totalSeconds: number
  awaySeconds: number
  includeAway: boolean
}>()
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
        <span class="font-mono text-[1.75rem] font-bold text-foreground">{{ formatDuration(totalSeconds) }}</span>
        <span class="text-[0.8rem] text-muted-foreground">
          {{ appSummaries.length }} 个应用<template v-if="awaySeconds > 0"> · {{ includeAway ? '含' : '另有' }}离开 {{ formatDuration(awaySeconds) }}</template>
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
