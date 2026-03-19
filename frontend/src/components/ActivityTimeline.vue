<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { getIconUrl } from '../api/index'
import type { AppUsageResponse } from '../api/index'
import { useTimelineDrag } from '../composables/useTimelineDrag'

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

const parsedUsageByApp = computed(() => {
  const map = new Map<number, ParsedSegment[]>()
  for (const u of props.usageData) {
    if (!u.appId || !u.startTime || !u.endTime) continue
    let arr = map.get(u.appId)
    if (!arr) { arr = []; map.set(u.appId, arr) }
    arr.push({ start: new Date(u.startTime).getTime(), end: new Date(u.endTime).getTime() })
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
      name: props.appNameMap.get(appId) || `App ${appId}`,
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
  for (const segments of parsedUsageByApp.value.values()) {
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
  <section class="panel timeline-panel">
    <div class="panel-header">
      <h2>活动时间线</h2>
      <div class="mode-toggle">
        <button 
          :class="['tg-btn', { active: mode === 'simple' }]" 
          @click="mode = 'simple'"
          title="24小时热力图"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7"></rect><rect x="14" y="3" width="7" height="7"></rect><rect x="14" y="14" width="7" height="7"></rect><rect x="3" y="14" width="7" height="7"></rect></svg>
        </button>
        <button 
          :class="['tg-btn', { active: mode === 'detailed' }]" 
          @click="mode = 'detailed'"
          title="详细时间线"
        >
          <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"><line x1="3" y1="12" x2="21" y2="12"></line><line x1="3" y1="6" x2="21" y2="6"></line><line x1="3" y1="18" x2="21" y2="18"></line></svg>
        </button>
      </div>
    </div>

    <!-- Simple Mode -->
    <div v-if="mode === 'simple'">
      <div class="timeline">
        <div
          v-for="h in 24"
          :key="h - 1"
          class="tl-block"
          :class="{ active: activeHours.has(h - 1) }"
          :title="`${String(h - 1).padStart(2, '0')}:00`"
        ></div>
      </div>
      <div class="tl-labels">
        <span>00</span>
        <span>06</span>
        <span>12</span>
        <span>18</span>
        <span>24</span>
      </div>
    </div>

    <!-- Detailed Mode -->
    <div v-else class="detailed-container" ref="timelineEl" @wheel="handleWheel">
      <!-- Minimap -->
      <div class="minimap">
        <div class="minimap-bg">
          <div 
            v-for="(burst, i) in minimapActivities"
            :key="i"
            class="minimap-burst"
            :style="{ left: burst.left, width: burst.width }"
          ></div>
        </div>
        <div class="minimap-window" :style="minimapRangeStyle" @mousedown="minimapPointerDown($event, 'center')" @touchstart.prevent="minimapPointerDown($event, 'center')">
          <div class="minimap-handle left" @mousedown.stop="minimapPointerDown($event, 'left')" @touchstart.stop.prevent="minimapPointerDown($event, 'left')"></div>
          <div class="minimap-handle right" @mousedown.stop="minimapPointerDown($event, 'right')" @touchstart.stop.prevent="minimapPointerDown($event, 'right')"></div>
        </div>
      </div>

      <!-- Main Timeline -->
      <div class="timeline-body" :class="{ dragging: isDraggingTimeline }" @mousedown="timelinePointerDown($event)" @touchstart="timelinePointerDown($event)">
        <div class="timeline-ticks">
          <div class="tick-ph"></div> <!-- Spacer for icon column -->
          <div class="tick-track">
            <div class="tick" v-for="t in ticks" :key="t.label" :style="{ left: t.percent + '%' }">
              <span class="tick-label">{{ t.label }}</span>
              <div class="tick-line"></div>
            </div>
          </div>
        </div>

        <TransitionGroup name="row-list" tag="div" class="timeline-rows">
          <div v-if="detailedRows.length === 0" key="empty" class="empty-state">
             当前时间范围内无活动记录，请拖拽或缩放上方缩略图更改范围
          </div>
          <div 
            v-for="row in detailedRows" 
            :key="row.appId" 
            class="timeline-row"
          >
            <div class="row-header">
              <img :src="getIconUrl(row.appId)" class="row-icon" @error="($event.target as HTMLImageElement).style.display = 'none'"/>
              <span class="row-name" :title="row.name">{{ row.name }}</span>
            </div>
            <div class="row-track">
              <!-- Event blocks (only visible segments are in DOM) -->
              <div 
                v-for="(seg, idx) in row.usages" 
                :key="idx"
                class="row-segment"
                :style="{
                  left: seg.left + '%',
                  width: seg.width + '%'
                }"
                :title="seg.title"
              ></div>
            </div>
          </div>
        </TransitionGroup>
      </div>
    </div>
  </section>
</template>

<style scoped>
.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1rem;
}

.panel-header h2 {
  margin-bottom: 0;
}

.mode-toggle {
  display: flex;
  background: #2a2a2a;
  border-radius: 6px;
  padding: 2px;
  gap: 2px;
}

.tg-btn {
  background: transparent;
  border: none;
  color: var(--text-dim);
  padding: 4px;
  border-radius: 4px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
}

.tg-btn:hover {
  color: var(--text);
  background: rgba(255, 255, 255, 0.05);
}

.tg-btn.active {
  color: var(--text);
  background: #444;
  box-shadow: 0 1px 3px rgba(0,0,0,0.3);
}

/* Simple Timeline Styles (Restored from App.vue) */
.timeline {
  display: flex;
  gap: 4px;
  height: 30px;
  margin-bottom: 0.5rem;
}
.tl-block {
  flex: 1;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 4px;
  transition: background 0.3s;
}
.tl-block.active {
  background: var(--accent);
  border-color: var(--accent);
}
.tl-labels {
  display: flex;
  justify-content: space-between;
  font-size: 0.75rem;
  color: var(--text-dim);
  font-family: var(--font-mono);
}

/* Detailed Timeline Styles */
.detailed-container {
  display: flex;
  flex-direction: column;
  gap: 12px;
  user-select: none;
}

.minimap {
  height: 24px;
  background: #1a1a1a;
  border: 1px solid var(--border);
  border-radius: 4px;
  position: relative;
  overflow: hidden;
}

.minimap-bg {
  position: absolute;
  top: 0; left: 0; right: 0; bottom: 0;
}

.minimap-burst {
  position: absolute;
  top: 6px;
  bottom: 6px;
  background: var(--accent, #3ee08e);
  opacity: 0.6;
  border-radius: 2px;
}

.minimap-window {
  position: absolute;
  top: 0;
  bottom: 0;
  background: rgba(88, 166, 255, 0.2);
  border: 1px solid rgba(88, 166, 255, 0.5);
  cursor: grab;
  box-sizing: border-box;
  touch-action: pan-y;
}

.minimap-window:active {
  cursor: grabbing;
}

.minimap-handle {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 8px;
  background: rgba(88, 166, 255, 0.8);
  cursor: ew-resize;
}

.minimap-handle.left { left: 0; }
.minimap-handle.right { right: 0; }

.timeline-body {
  border: 1px solid var(--border);
  border-radius: 6px;
  background: #1a1a1a;
  overflow: hidden;
  cursor: grab;
  touch-action: pan-y;
}

.timeline-body.dragging {
  cursor: grabbing;
}

.timeline-ticks {
  display: flex;
  height: 24px;
  background: #222;
  border-bottom: 1px solid var(--border);
}

.tick-ph {
  width: 120px; /* Width of the icon column */
  flex-shrink: 0;
  border-right: 1px solid var(--border);
}

.tick-track {
  flex: 1;
  position: relative;
}

.tick {
  position: absolute;
  top: 0;
  bottom: 0;
  transform: translateX(-50%);
  display: flex;
  flex-direction: column;
  align-items: center;
  pointer-events: none;
}

.tick-label {
  font-size: 0.65rem;
  color: var(--text-dim);
  margin-top: 2px;
  font-family: var(--font-mono);
}

.tick-line {
  position: absolute;
  top: 24px;
  bottom: -500px; /* Arbitrary long line downwards */
  width: 1px;
  background: rgba(255, 255, 255, 0.05);
  z-index: 0;
}

.timeline-rows {
  max-height: 220px; /* Approx 5 rows (40px per row) */
  overflow-y: auto;
  position: relative;
  z-index: 1;
}

/* Custom Scrollbar for rows */
.timeline-row {
  display: flex;
  height: 40px;
  border-bottom: 1px solid rgba(255,255,255,0.05);
}

.timeline-row:last-child {
  border-bottom: none;
}

.row-header {
  width: 120px;
  flex-shrink: 0;
  border-right: 1px solid var(--border);
  background: #1e1e1e;
  display: flex;
  align-items: center;
  padding: 0 8px;
  gap: 8px;
  z-index: 2; /* keeps it above tick lines visually */
}

.row-icon {
  width: 20px;
  height: 20px;
  border-radius: 4px;
  object-fit: contain;
}

.row-name {
  font-size: 0.75rem;
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  flex: 1;
}

.row-track {
  flex: 1;
  position: relative;
  /* background: #1a1a1a; */
}

.row-segment {
  position: absolute;
  top: 10px;
  height: 20px;
  background: var(--accent);
  border-radius: 3px;
  opacity: 0.8;
  cursor: pointer;
}

.row-segment:hover {
  opacity: 1;
  z-index: 3;
}

.empty-state {
  padding: 2rem;
  text-align: center;
  color: var(--text-dim);
  font-size: 0.8rem;
}

/* Row reorder animation */
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
