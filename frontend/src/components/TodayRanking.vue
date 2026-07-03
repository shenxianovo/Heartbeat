<script setup lang="ts">
import { getIconUrl } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'
import { Card } from '@/components/ui/card'

defineProps<{
  appSummaries: { appId: number; appName: string; totalSeconds: number }[]
  maxSeconds: number
}>()

const emit = defineEmits<{ select: [app: { appId: number; appName: string; totalSeconds: number }] }>()
</script>

<template>
  <Card class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">今日应用时长排行</h2>

      <div
        v-if="appSummaries.length"
        class="flex max-h-[200px] flex-col gap-3 overflow-y-auto pr-1 min-[900px]:max-h-[280px] min-[1200px]:max-h-[340px]"
      >
        <div
          v-for="(app, i) in appSummaries"
          :key="app.appName"
          class="flex cursor-pointer flex-col gap-1 rounded-md transition-colors hover:bg-accent/50"
          @click="emit('select', app)"
        >
          <div class="flex items-center gap-2 text-[0.85rem]">
            <span class="w-6 text-center text-xs font-semibold text-muted-foreground">{{ i + 1 }}</span>
            <img
              :src="getIconUrl(app.appId)"
              class="h-[18px] w-[18px] rounded object-contain"
              @error="($event.target as HTMLImageElement).style.display = 'none'"
            />
            <span class="flex-1 truncate">{{ app.appName }}</span>
            <span class="font-mono text-[0.8rem] text-muted-foreground">{{ formatDuration(app.totalSeconds) }}</span>
          </div>
          <div class="ml-8 h-1 overflow-hidden rounded-sm bg-secondary">
            <div
              class="h-full rounded-sm bg-primary"
              :style="{ width: `${(app.totalSeconds / maxSeconds) * 100}%` }"
            ></div>
          </div>
        </div>
      </div>

      <div v-else class="py-8 text-center text-[0.9rem] text-muted-foreground">暂无数据</div>
    </div>
  </Card>
</template>
