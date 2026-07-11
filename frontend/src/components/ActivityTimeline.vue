<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { getIconUrl } from '../api/index'
import type { AppUsageResponse } from '../api/index'
import { useTimelineDrag } from '../composables/useTimelineDrag'
import { Card } from '@/components/ui/card'
import { LayoutGrid, AlignJustify } from 'lucide-vue-next'
import { AWAY_APP } from '../appLabels'

const props = defineProps<{
  activeHours: Set<number>,
  usageData: AppUsageResponse[],
  appNameMap: Map<number, string>,
  selectedDate: string,
  isToday: boolean
}>()

const mode = ref<'simple' | 'detailed'>('detailed')

// --- Detailed Mode State ---
const containerWidth = ref(0)
const timelineEl = ref<HTMLElement | null>(null)
const viewStart = ref<number>(0)
const viewEnd = ref<number>(0)

const ONE_HOUR = 60 * 60 * 1000

// --- Drag interaction (composable) ---
const {
  isDraggingTimeline,
  handleWheel,
  timelinePointerDown,
  minimapPointerDown,
} = useTimelineDrag(viewStart, viewEnd, timelineEl, computed(() => props.selectedDate))

// Initialize view bounds
const initViewBounds = () => {
  const d = new Date(props.selectedDate)
  const baseTime = d.getTime()

  if (props.isToday) {
    const now = Date.now()
    viewStart.value = now - ONE_HOUR
    viewEnd.value = now + ONE_HOUR
  } else {
    const firstEvent = props.usageData.find(u => u.startTime)
    if (firstEvent && firstEvent.startTime) {
      const startT = new Date(firstEvent.startTime).getTime()
      viewStart.value = startT - ONE_HOUR
      viewEnd.value = startT + ONE_HOUR
    } else {
      viewStart.value = baseTime + 11 * ONE_HOUR
      viewEnd.value = baseTime + 13 * ONE_HOUR
    }
  }
}

watch(
  [() => props.selectedDate, () => props.isToday, () => props.usageData],
  () => { initViewBounds() },
  { immediate: true }
)

onMounted(() => {
  initViewBounds()
  if (timelineEl.value) {
    containerWidth.value = timelineEl.value.clientWidth
    window.addEventListener('resize', handleResize)
  }
})

onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
})

const handleResize = () => {
  if (timelineEl.value) containerWidth.value = timelineEl.value.clientWidth
}

// ========== Pre-parsed data (only recomputes when usageData changes) ==========

interface ParsedSegment { start: number; end: number }

// away（离开）段对应的 appId 集合 —— 由 appName === '__away__' 识别。
const awayAppIds = computed(() => {
  const set = new Set<number>()
  for (const u of props.usageData) {
    if (u.appId && u.appName === AWAY_APP) set.add(u.appId)
  }
  return set
})

const isAwayApp = (appId: number) => awayAppIds.value.has(appId)

// 同一 App 相邻段合并阈值：标题切段首尾相接，仅 <1s 丢段会留小缝，
// ≤2s 缝合这些缝隙，又不会把真实切走别的 App 画成连续使用。
const MERGE_GAP_MS = 2000

const parsedUsageByApp = computed(() => {
  const map = new Map<number, ParsedSegment[]>()
  for (const u of props.usageData) {
    if (!u.appId || !u.startTime || !u.endTime) continue
    let arr = map.get(u.appId)
    if (!arr) { arr = []; map.set(u.appId, arr) }
    arr.push({ start: new Date(u.startTime).getTime(), end: new Date(u.endTime).getTime() })
  }
  // 每个 App 内按开始时间排序，间隙 ≤2s 的相邻段合并为一段（标题不同不切分）
  for (const [appId, segments] of map) {
    segments.sort((a, b) => a.start - b.start)
    const merged: ParsedSegment[] = []
    for (const seg of segments) {
      const last = merged[merged.length - 1]
      if (last && seg.start - last.end <= MERGE_GAP_MS) {
        last.end = Math.max(last.end, seg.end)
      } else {
        merged.push({ ...seg })
      }
    }
    map.set(appId, merged)
  }
  return map
})

// ========== View-dependent computeds ==========

const activeAppsInView = computed(() => {
  const appDurations: [number, number][] = []
  const vs = viewStart.value, ve = viewEnd.value
  for (const [appId, segments] of parsedUsageByApp.value) {
    let totalDur = 0
    for (const seg of segments) {
      if (seg.end < vs || seg.start > ve) continue
      totalDur += Math.min(seg.end, ve) - Math.max(seg.start, vs)
    }
    if (totalDur > 0) appDurations.push([appId, totalDur])
  }
  appDurations.sort((a, b) => b[1] - a[1])
  return appDurations.map(d => d[0])
})

const fmtTime = (time: number) => {
  const d = new Date(time)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const detailedRows = computed(() => {
  const vs = viewStart.value, ve = viewEnd.value
  const totalRange = ve - vs
  if (totalRange <= 0) return []

  return activeAppsInView.value.map(appId => {
    const allSegments = parsedUsageByApp.value.get(appId) || []
    const visibleUsages: { start: number; end: number; left: number; width: number; title: string }[] = []
    for (const seg of allSegments) {
      if (seg.end < vs || seg.start > ve) continue
      const l = Math.max(0, Math.min(100, ((seg.start - vs) / totalRange) * 100))
      const r = Math.max(0, Math.min(100, ((seg.end - vs) / totalRange) * 100))
      visibleUsages.push({
        start: seg.start, end: seg.end,
        left: l, width: Math.max(0.5, r - l),
        title: `${fmtTime(seg.start)} - ${fmtTime(seg.end)}`
      })
    }
    return {
      appId,
      name: isAwayApp(appId) ? '离开' : (props.appNameMap.get(appId) || `App ${appId}`),
      isAway: isAwayApp(appId),
      usages: visibleUsages
    }
  })
})

// Adaptive tick intervals based on available width
const ticks = computed(() => {
  const result: { percent: number; label: string }[] = []
  const range = viewEnd.value - viewStart.value
  if (range <= 0) return result

  const trackWidth = (containerWidth.value || 800) - 120
  const minTickSpacingPx = 70
  const maxTicks = Math.max(2, Math.floor(trackWidth / minTickSpacingPx))

  // Nice intervals in ms: 1m, 2m, 5m, 10m, 15m, 30m, 1h, 2h, 3h, 6h
  const niceIntervals = [
    60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000,
    3_600_000, 7_200_000, 10_800_000, 21_600_000
  ]
  let interval = niceIntervals[niceIntervals.length - 1]
  for (const ni of niceIntervals) {
    if (range / ni <= maxTicks) { interval = ni; break }
  }

  const firstTick = Math.ceil(viewStart.value / interval) * interval
  for (let t = firstTick; t <= viewEnd.value; t += interval) {
    result.push({
      percent: ((t - viewStart.value) / range) * 100,
      label: fmtTime(t)
    })
  }
  return result
})

// ========== Minimap ==========

const dayStartMs = computed(() => new Date(props.selectedDate).setHours(0, 0, 0, 0))

const minimapRangeStyle = computed(() => {
  const day = 24 * 60 * 60 * 1000
  const l = ((viewStart.value - dayStartMs.value) / day) * 100
  const w = ((viewEnd.value - viewStart.value) / day) * 100
  return { left: `${Math.max(0, l)}%`, width: `${Math.min(100 - l, w)}%` }
})

const minimapActivities = computed(() => {
  const day = 24 * 60 * 60 * 1000
  const dayS = dayStartMs.value
  const rawIntervals: { start: number; end: number }[] = []
  for (const [appId, segments] of parsedUsageByApp.value) {
    if (isAwayApp(appId)) continue // away 不算活跃，不进缩略图
    for (const seg of segments) rawIntervals.push(seg)
  }
  rawIntervals.sort((a, b) => a.start - b.start)
  if (rawIntervals.length === 0) return []

  const merged: { start: number; end: number }[] = []
  let current = { ...rawIntervals[0] }
  for (let i = 1; i < rawIntervals.length; i++) {
    const next = rawIntervals[i]
    if (next.start <= current.end + 60000) {
      current.end = Math.max(current.end, next.end)
    } else {
      merged.push(current)
      current = { ...next }
    }
  }
  merged.push(current)

  return merged.map(iv => {
    const startP = Math.max(0, ((iv.start - dayS) / day) * 100)
    const endP = Math.min(100, ((iv.end - dayS) / day) * 100)
    return { left: `${startP}%`, width: `${Math.max(0.2, endP - startP)}%` }
  })
})
</script>

<template>
  <Card class="mb-6 gap-4 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-4 px-5">
      <div class="flex items-center justify-between">
        <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">活动时间线</h2>
        <div class="flex gap-0.5 rounded-full border border-glass-border bg-glass p-0.5 shadow-sm backdrop-blur-md">
          <button
            class="flex cursor-pointer items-center justify-center rounded-full p-1.5 transition-colors"
            :class="mode === 'simple' ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:bg-accent hover:text-foreground'"
            @click="mode = 'simple'"
            title="24小时热力图"
          >
            <LayoutGrid :size="16" />
          </button>
          <button
            class="flex cursor-pointer items-center justify-center rounded-full p-1.5 transition-colors"
            :class="mode === 'detailed' ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:bg-accent hover:text-foreground'"
            @click="mode = 'detailed'"
            title="详细时间线"
          >
            <AlignJustify :size="16" />
          </button>
        </div>
      </div>

      <!-- Simple Mode -->
      <div v-if="mode === 'simple'">
        <div class="mb-2 flex h-[30px] gap-1">
          <div
            v-for="h in 24"
            :key="h - 1"
            class="flex-1 rounded border transition-colors duration-300"
            :class="activeHours.has(h - 1) ? 'border-primary bg-primary' : 'border-border bg-card'"
            :title="`${String(h - 1).padStart(2, '0')}:00`"
          ></div>
        </div>
        <div class="flex justify-between font-mono text-xs text-muted-foreground">
          <span>00</span>
          <span>06</span>
          <span>12</span>
          <span>18</span>
          <span>24</span>
        </div>
      </div>

      <!-- Detailed Mode -->
      <div v-else class="flex select-none flex-col gap-3" ref="timelineEl" @wheel="handleWheel">
        <!-- Minimap -->
        <div class="relative h-6 overflow-hidden rounded border border-border bg-secondary">
          <div class="absolute inset-0">
            <div
              v-for="(burst, i) in minimapActivities"
              :key="i"
              class="absolute bottom-1.5 top-1.5 rounded-sm bg-accent-3 opacity-60"
              :style="{ left: burst.left, width: burst.width }"
            ></div>
          </div>
          <div
            class="absolute bottom-0 top-0 box-border cursor-grab touch-pan-y border border-primary bg-primary-soft active:cursor-grabbing"
            :style="minimapRangeStyle"
            @mousedown="minimapPointerDown($event, 'center')"
            @touchstart.prevent="minimapPointerDown($event, 'center')"
          >
            <div
              class="absolute bottom-0 top-0 left-0 w-2 cursor-ew-resize bg-primary"
              @mousedown.stop="minimapPointerDown($event, 'left')"
              @touchstart.stop.prevent="minimapPointerDown($event, 'left')"
            ></div>
            <div
              class="absolute bottom-0 top-0 right-0 w-2 cursor-ew-resize bg-primary"
              @mousedown.stop="minimapPointerDown($event, 'right')"
              @touchstart.stop.prevent="minimapPointerDown($event, 'right')"
            ></div>
          </div>
        </div>

        <!-- Main Timeline -->
        <div
          class="overflow-hidden rounded-md border border-border bg-secondary touch-pan-y"
          :class="isDraggingTimeline ? 'cursor-grabbing' : 'cursor-grab'"
          @mousedown="timelinePointerDown($event)"
          @touchstart="timelinePointerDown($event)"
        >
          <div class="flex h-6 border-b border-border bg-muted">
            <div class="w-[80px] shrink-0 border-r border-border min-[640px]:w-[120px]"></div>
            <div class="relative flex-1">
              <div
                class="pointer-events-none absolute bottom-0 top-0 flex -translate-x-1/2 flex-col items-center"
                v-for="t in ticks"
                :key="t.label"
                :style="{ left: t.percent + '%' }"
              >
                <span class="mt-0.5 font-mono text-[0.65rem] text-muted-foreground">{{ t.label }}</span>
                <div class="absolute top-6 -bottom-[500px] z-0 w-px bg-border"></div>
              </div>
            </div>
          </div>

          <TransitionGroup
            name="row-list"
            tag="div"
            class="timeline-rows relative z-[1] max-h-[220px] overflow-y-auto min-[900px]:max-h-[320px] min-[1200px]:max-h-[400px]"
          >
            <div
              v-if="detailedRows.length === 0"
              key="empty"
              class="p-8 text-center text-[0.8rem] text-muted-foreground"
            >
              当前时间范围内无活动记录，请拖拽或缩放上方缩略图更改范围
            </div>
            <div
              v-for="row in detailedRows"
              :key="row.appId"
              class="flex h-10 border-b border-border last:border-b-0"
            >
              <!-- row-header / timeline-rows 是 useTimelineDrag 的功能性选择器锚点，不是样式 -->
              <div class="row-header z-[2] flex w-[80px] shrink-0 items-center gap-2 border-r border-border bg-muted px-2 min-[640px]:w-[120px]">
                <img v-if="!row.isAway" :src="getIconUrl(row.appId)" class="h-5 w-5 rounded object-contain" @error="($event.target as HTMLImageElement).style.display = 'none'"/>
                <span v-else class="flex h-5 w-5 shrink-0 items-center justify-center text-muted-foreground">💤</span>
                <span class="flex-1 truncate text-[0.75rem]" :class="row.isAway ? 'text-muted-foreground' : 'text-foreground'" :title="row.name">{{ row.name }}</span>
              </div>
              <div class="relative flex-1">
                <div
                  v-for="(seg, idx) in row.usages"
                  :key="idx"
                  class="absolute top-2.5 h-5 cursor-pointer rounded-sm opacity-80 hover:z-[3] hover:opacity-100"
                  :class="row.isAway ? 'bg-muted-foreground/40' : 'bg-primary'"
                  :style="{ left: seg.left + '%', width: seg.width + '%' }"
                  :title="seg.title"
                ></div>
              </div>
            </div>
          </TransitionGroup>
        </div>
      </div>
    </div>
  </Card>
</template>

<style scoped>
/* TransitionGroup 行重排动画依赖具名 class，保留为 scoped CSS */
.row-list-move {
  transition: transform 0.3s ease;
}
.row-list-enter-active {
  transition: opacity 0.2s ease;
}
.row-list-leave-active {
  transition: opacity 0.15s ease;
  position: absolute;
  width: 100%;
}
.row-list-enter-from,
.row-list-leave-to {
  opacity: 0;
}
</style>
