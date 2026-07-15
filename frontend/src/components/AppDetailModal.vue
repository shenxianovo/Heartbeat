<script setup lang="ts">
import { computed, watch, onMounted, onUnmounted } from 'vue'
import { getIconUrl, fetchPublicSegments } from '../api/index'
import type { AppUsageResponse, SegmentResponse } from '../api/index'
import { useAsyncData } from '../composables/useAsyncData'
import { formatDuration } from '../composables/useHeartbeat'
import { formatTitle } from '../titleFormatters'
import { upgradeBreakdown } from '../labelUpgrade'
import { toPluginSegs, toSystemSegs, toReplaySegs } from '../segmentAdapters'
import { envelope, buildTracks, type Track } from '../timeline/replayModel'
import type { Interval } from '../timeline/timelineModel'
import { niceTicks } from '../timeline/timeScale'
import { X } from 'lucide-vue-next'

const props = defineProps<{
  username: string
  deviceId: number
  selectedDate: string
  app: { appId: number; appName: string; totalSeconds: number }
  usageData: AppUsageResponse[]
}>()

const emit = defineEmits<{ close: [] }>()

// ── 插件段(非 system 轨) ──
const segs = useAsyncData<SegmentResponse[]>(() => {
  const dateObj = new Date(props.selectedDate + 'T00:00:00')
  return fetchPublicSegments(props.username, {
    deviceId: props.deviceId,
    appId: props.app.appId,
    start: dateObj.toISOString(),
    end: new Date(dateObj.getTime() + 86400000).toISOString(),
  })
}, [])
const pluginSegments = segs.data
const loading = segs.pending
const segmentsFailed = computed(() => segs.error.value !== null)

watch(() => props.app.appId, () => segs.run(), { immediate: true })

// ── 多轨回放（静态视窗 = 全部轨道数据的时间包络；模型在 timeline/replayModel.ts）──

const systemSegments = computed(() =>
  props.usageData.filter(u => u.appId === props.app.appId && u.startTime && u.endTime)
)

const viewBounds = computed(() => {
  const intervals: Interval[] = []
  for (const u of systemSegments.value) {
    intervals.push({ start: u.startTime!.getTime(), end: u.endTime!.getTime() })
  }
  for (const s of pluginSegments.value) {
    if (!s.startTime || !s.endTime) continue
    intervals.push({ start: s.startTime.getTime(), end: s.endTime.getTime() })
  }
  return envelope(intervals)
})

// system 主轨在前，插件轨按副本分 lane（browser: attributes.windowId）
const tracks = computed<Track[]>(() => {
  const vb = viewBounds.value
  if (!vb) return []
  return buildTracks(toReplaySegs(systemSegments.value, pluginSegments.value), vb)
})

// ── 标题明细（ADR-019 标签升级）──
// system 段有重叠插件段时标签升级为页面标题/URL，无覆盖的时间窗口 fallback 到窗口标题。

const breakdown = computed(() =>
  upgradeBreakdown(
    toSystemSegs(systemSegments.value),
    toPluginSegs(pluginSegments.value),
    formatTitle,
  )
)

const timeTicks = computed(() => {
  const vb = viewBounds.value
  return vb ? niceTicks(vb.start, vb.end, 10) : []
})

// ── 全局弹窗行为:Esc 关闭 + 锁背景滚动 ──
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}
onMounted(() => {
  document.addEventListener('keydown', onKeydown)
  document.body.style.overflow = 'hidden'
})
onUnmounted(() => {
  document.removeEventListener('keydown', onKeydown)
  document.body.style.overflow = ''
})
</script>

<template>
  <Teleport to="body">
    <div
      class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm min-[640px]:p-8"
      @click.self="emit('close')"
    >
      <div class="flex max-h-full w-full max-w-4xl flex-col overflow-hidden rounded-xl border border-border bg-card shadow-2xl">
        <!-- Header -->
        <div class="flex items-center gap-3 border-b border-border px-5 py-4">
          <img
            :src="getIconUrl(username, app.appId)"
            class="h-7 w-7 rounded object-contain"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <span class="truncate text-base font-semibold">{{ app.appName }}</span>
          <span class="font-mono text-sm text-muted-foreground">{{ formatDuration(app.totalSeconds) }}</span>
          <button
            class="ml-auto flex cursor-pointer items-center justify-center rounded-full p-1.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
            @click="emit('close')"
          >
            <X :size="18" />
          </button>
        </div>

        <div class="flex flex-col gap-5 overflow-y-auto px-5 py-4">
          <!-- 多轨回放 -->
          <section>
            <h3 class="mb-2 text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">回放</h3>
            <div v-if="tracks.length && viewBounds" class="overflow-hidden rounded-md border border-border bg-secondary">
              <!-- 刻度行 -->
              <div class="flex h-6 border-b border-border bg-muted">
                <div class="w-[80px] shrink-0 border-r border-border"></div>
                <div class="relative flex-1">
                  <span
                    v-for="t in timeTicks"
                    :key="t.label"
                    class="pointer-events-none absolute mt-0.5 -translate-x-1/2 font-mono text-[0.65rem] text-muted-foreground"
                    :style="{ left: t.percent + '%' }"
                  >{{ t.label }}</span>
                </div>
              </div>
              <!-- 轨道行:system 主轨在前,插件轨挂在下方;轨内按副本分 lane（如浏览器窗口） -->
              <div
                v-for="track in tracks"
                :key="track.source"
                class="flex border-b border-border last:border-b-0"
              >
                <div class="flex w-[80px] shrink-0 items-center border-r border-border bg-muted px-2">
                  <span class="truncate font-mono text-[0.7rem] text-muted-foreground">{{ track.source }}</span>
                </div>
                <div class="flex-1">
                  <div
                    v-for="(lane, li) in track.lanes"
                    :key="li"
                    class="relative h-9 border-b border-dashed border-border/50 last:border-b-0"
                  >
                    <template v-for="(bar, i) in lane.bars" :key="i">
                      <!-- 点事件:菱形标记 -->
                      <div
                        v-if="bar.isPoint"
                        class="absolute top-1/2 z-[1] h-2 w-2 -translate-x-1/2 -translate-y-1/2 rotate-45 cursor-pointer bg-accent-3 hover:z-[2] hover:scale-125"
                        :style="{ left: bar.left + '%' }"
                        :title="bar.tooltip"
                      ></div>
                      <!-- 段:横条 -->
                      <div
                        v-else
                        class="absolute top-2 h-5 cursor-pointer rounded-sm opacity-80 hover:z-[2] hover:opacity-100"
                        :class="track.source === 'system' ? 'bg-primary' : 'bg-accent-3'"
                        :style="{ left: bar.left + '%', width: bar.width + '%' }"
                        :title="bar.tooltip"
                      ></div>
                    </template>
                  </div>
                </div>
              </div>
            </div>
            <div v-else class="rounded-md border border-border bg-secondary py-6 text-center text-[0.8rem] text-muted-foreground">
              {{ loading ? '加载中…' : segmentsFailed ? '回放数据加载失败' : '当日无回放数据' }}
            </div>
          </section>

          <!-- 标题明细（插件覆盖时段升级为页面级，其余窗口标题 fallback） -->
          <section>
            <h3 class="mb-2 text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">标题明细</h3>
            <div class="flex flex-col gap-2">
              <div
                v-for="(t, ti) in breakdown"
                :key="ti"
                class="flex items-center gap-2 text-[0.8rem]"
                :class="t.category === 'system' ? 'opacity-50' : ''"
              >
                <span
                  v-if="t.upgraded"
                  class="h-1.5 w-1.5 shrink-0 rounded-full bg-accent-3"
                  title="页面级明细（浏览器插件）"
                ></span>
                <span class="flex min-w-0 flex-1 flex-col">
                  <span class="truncate" :class="t.title ? '' : 'text-muted-foreground italic'" :title="t.title">{{ t.title || '无标题窗口' }}</span>
                  <span v-if="t.secondary" class="truncate text-[0.65rem] text-muted-foreground" :title="t.secondary">{{ t.secondary }}</span>
                </span>
                <span class="shrink-0 text-[0.7rem] text-muted-foreground">×{{ t.count }}</span>
                <span class="shrink-0 font-mono text-[0.75rem] text-muted-foreground">{{ formatDuration(t.totalSeconds) }}</span>
              </div>
              <div
                v-if="breakdown.length === 0"
                class="py-2 text-center text-[0.8rem] text-muted-foreground"
              >
                无标题明细
              </div>
            </div>
          </section>
        </div>
      </div>
    </div>
  </Teleport>
</template>
