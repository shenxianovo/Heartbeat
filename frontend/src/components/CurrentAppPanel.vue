<script setup lang="ts">
import { getIconUrl } from '../api/index'
import { getAppLabel } from '../appLabels'
import { Card } from '@/components/ui/card'

defineProps<{
  isToday: boolean
  isAlive: boolean
  currentApp: string | null
  currentAppId: number | null
}>()
</script>

<template>
  <Card v-if="isToday" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">当前使用</h2>

      <!-- 在线 + 有前台应用 -->
      <div v-if="isAlive && currentApp" class="flex items-center gap-3 py-1">
        <span class="status-dot alive"></span>
        <img
          v-if="currentAppId"
          :src="getIconUrl(currentAppId)"
          class="h-7 w-7 shrink-0 object-contain"
          @error="($event.target as HTMLImageElement).style.display = 'none'"
        />
        <div class="flex flex-col gap-0.5">
          <span class="text-[1.1rem] font-semibold">{{ currentApp }}</span>
          <span v-if="getAppLabel(currentApp)" class="text-[0.8rem] text-muted-foreground">
            {{ getAppLabel(currentApp) }}
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
