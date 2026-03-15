<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { getIconUrl } from '../api/index'
import type { AppUsageResponse } from '../api/index'

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

// Time range in milliseconds
const viewStart = ref<number>(0)
const viewEnd = ref<number>(0)
const isDraggingMinimap = ref(false)
const dragType = ref<'left' | 'right' | 'center' | null>(null)
const dragStartX = ref(0)
const dragStartViewStart = ref(0)
const dragStartViewEnd = ref(0)

const ONE_HOUR = 60 * 60 * 1000

// Initialize view bounds
const initViewBounds = () => {
  const d = new Date(props.selectedDate)
  const baseTime = d.getTime()
  
  if (props.isToday) {
    const now = Date.now()
    viewStart.value = now - ONE_HOUR
    viewEnd.value = now + ONE_HOUR
  } else {
    // If historically, find the first event or default to 12PM
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
  () => {
    initViewBounds()
  },
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

// Compute active apps in the current view range
const activeAppsInView = computed(() => {
  const appDurations = new Map<number, number>() // appId -> total duration in view
  
  for (const usage of props.usageData) {
    if (!usage.appId || !usage.startTime || !usage.endTime) continue
    const s = new Date(usage.startTime).getTime()
    const e = new Date(usage.endTime).getTime()
    
    // Check intersection with view
    if (e < viewStart.value || s > viewEnd.value) continue
    
    const intersectS = Math.max(s, viewStart.value)
    const intersectE = Math.min(e, viewEnd.value)
    const dur = intersectE - intersectS
    
    if (dur > 0) {
      appDurations.set(usage.appId, (appDurations.get(usage.appId) || 0) + dur)
    }
  }
  
  // Sort by highest duration in view first
  const sorted = Array.from(appDurations.entries()).sort((a, b) => b[1] - a[1])
  return sorted.map(s => s[0])
})

const detailedRows = computed(() => {
  return activeAppsInView.value.map(appId => {
    const usagesList = props.usageData.filter(u => u.appId === appId && u.startTime && u.endTime)
    return {
      appId,
      name: props.appNameMap.get(appId) || `App ${appId}`,
      usages: usagesList.map(u => ({
        start: new Date(u.startTime!).getTime(),
        end: new Date(u.endTime!).getTime()
      }))
    }
  })
})

const calculateLeft = (time: number) => {
  const totalRange = viewEnd.value - viewStart.value
  if (totalRange <= 0) return 0
  return Math.max(0, Math.min(100, ((time - viewStart.value) / totalRange) * 100))
}

const calculateWidth = (start: number, end: number) => {
  const l = calculateLeft(start)
  const r = calculateLeft(end)
  return Math.max(0.5, r - l) // at least 0.5% width to be visible
}

const formatTime = (time: number) => {
  const d = new Date(time)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const getTicks = () => {
  const ticks: {percent: number, label: string}[] = []
  const range = viewEnd.value - viewStart.value
  if (range <= 0) return ticks
  
  const segments = 6
  for (let i = 0; i <= segments; i++) {
    const t = viewStart.value + (range / segments) * i
    ticks.push({
      percent: (i / segments) * 100,
      label: formatTime(t)
    })
  }
  return ticks
}

// Interactivity for zooming and panning
const handleWheel = (e: WheelEvent) => {
  const range = viewEnd.value - viewStart.value
  
  if (e.ctrlKey || e.metaKey) {
    e.preventDefault()
    // Zoom
    const zoomFactor = e.deltaY > 0 ? 1.2 : 0.8
    const mouseX = e.offsetX
    const cw = timelineEl.value?.clientWidth || 800
    const pivotPercent = mouseX / cw
    const pivotTime = viewStart.value + range * pivotPercent
    
    let newStart = pivotTime - (pivotTime - viewStart.value) * zoomFactor
    let newEnd = pivotTime + (viewEnd.value - pivotTime) * zoomFactor
    
    // Bounds check
    const minRange = 5 * 60 * 1000 // 5 mins
    const maxRange = 24 * 60 * 60 * 1000 // 24 hours
    const dayStart = new Date(props.selectedDate).setHours(0,0,0,0)
    const dayEnd = dayStart + 24 * 60 * 60 * 1000
    
    if (newEnd - newStart < minRange) {
      newStart = pivotTime - minRange * pivotPercent
      newEnd = pivotTime + minRange * (1 - pivotPercent)
    }
    if (newEnd - newStart > maxRange) {
      newStart = dayStart
      newEnd = dayEnd
    }
    
    viewStart.value = Math.max(dayStart, newStart)
    viewEnd.value = Math.min(dayEnd, newEnd)
    
  } else if (Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
    e.preventDefault()
    // Pan horizontally
    const shift = e.deltaX * (range / 1000) // pan sensitively
    const dayStart = new Date(props.selectedDate).setHours(0,0,0,0)
    const dayEnd = dayStart + 24 * 60 * 60 * 1000
    
    let newStart = viewStart.value + shift
    let newEnd = viewEnd.value + shift
    
    if (newStart < dayStart) {
      newStart = dayStart
      newEnd = newStart + range
    }
    if (newEnd > dayEnd) {
      newEnd = dayEnd
      newStart = newEnd - range
    }
    
    viewStart.value = newStart
    viewEnd.value = newEnd
  }
}

// Minimap dragging
const minimapMousedown = (e: MouseEvent, type: 'left' | 'right' | 'center') => {
  isDraggingMinimap.value = true
  dragType.value = type
  dragStartX.value = e.clientX
  dragStartViewStart.value = viewStart.value
  dragStartViewEnd.value = viewEnd.value
  window.addEventListener('mousemove', minimapMousemove)
  window.addEventListener('mouseup', minimapMouseup)
}

const minimapMousemove = (e: MouseEvent) => {
  if (!isDraggingMinimap.value) return
  
  const deltaX = e.clientX - dragStartX.value
  const minimapWidth = timelineEl.value?.clientWidth || 800
  const dayRange = 24 * 60 * 60 * 1000
  const timeDelta = (deltaX / minimapWidth) * dayRange
  
  const dayStart = new Date(props.selectedDate).setHours(0,0,0,0)
  const dayEnd = dayStart + dayRange
  const minRange = 5 * 60 * 1000
  
  if (dragType.value === 'center') {
    let newS = dragStartViewStart.value + timeDelta
    let newE = dragStartViewEnd.value + timeDelta
    if (newS < dayStart) {
      newS = dayStart
      newE = newS + (dragStartViewEnd.value - dragStartViewStart.value)
    }
    if (newE > dayEnd) {
      newE = dayEnd
      newS = newE - (dragStartViewEnd.value - dragStartViewStart.value)
    }
    viewStart.value = newS
    viewEnd.value = newE
  } else if (dragType.value === 'left') {
    let newS = dragStartViewStart.value + timeDelta
    if (newS < dayStart) newS = dayStart
    if (newS > viewEnd.value - minRange) newS = viewEnd.value - minRange
    viewStart.value = newS
  } else if (dragType.value === 'right') {
    let newE = dragStartViewEnd.value + timeDelta
    if (newE > dayEnd) newE = dayEnd
    if (newE < viewStart.value + minRange) newE = viewStart.value + minRange
    viewEnd.value = newE
  }
}

const minimapMouseup = () => {
  isDraggingMinimap.value = false
  dragType.value = null
  window.removeEventListener('mousemove', minimapMousemove)
  window.removeEventListener('mouseup', minimapMouseup)
}

// Minimap calculations
const dayStartMs = computed(() => {
  return new Date(props.selectedDate).setHours(0,0,0,0)
})

const minimapRangeStyle = computed(() => {
  const day = 24 * 60 * 60 * 1000
  const l = ((viewStart.value - dayStartMs.value) / day) * 100
  const w = ((viewEnd.value - viewStart.value) / day) * 100
  return {
    left: `${Math.max(0, l)}%`,
    width: `${Math.min(100 - l, w)}%`
  }
})

// Minimap activities (just visual bars to show where there is some activity)
const minimapActivities = computed(() => {
  const day = 24 * 60 * 60 * 1000
  const dayS = dayStartMs.value
  
  // Merge intervals to avoid too many DOM elements
  const intervals: {start: number, end: number}[] = []
  
  // First collect all valid intervals
  const rawIntervals = props.usageData
    .filter(u => u.startTime && u.endTime)
    .map(u => ({
      start: new Date(u.startTime!).getTime(),
      end: new Date(u.endTime!).getTime()
    }))
    .sort((a, b) => a.start - b.start)
    
  if (rawIntervals.length === 0) return []
  
  // Merge overlapping or close intervals
  let current = { ...rawIntervals[0] }
  for (let i = 1; i < rawIntervals.length; i++) {
    const next = rawIntervals[i]
    if (next.start <= current.end + 60000) { // close within 1min
      current.end = Math.max(current.end, next.end)
    } else {
      intervals.push(current)
      current = { ...next }
    }
  }
  intervals.push(current)
  
  return intervals.map(iv => {
    const startP = Math.max(0, ((iv.start - dayS) / day) * 100)
    const endP = Math.min(100, ((iv.end - dayS) / day) * 100)
    return {
      left: `${startP}%`,
      width: `${Math.max(0.2, endP - startP)}%`
    }
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
        <div class="minimap-window" :style="minimapRangeStyle" @mousedown="minimapMousedown($event, 'center')">
          <div class="minimap-handle left" @mousedown.stop="minimapMousedown($event, 'left')"></div>
          <div class="minimap-handle right" @mousedown.stop="minimapMousedown($event, 'right')"></div>
        </div>
      </div>

      <!-- Main Timeline -->
      <div class="timeline-body">
        <div class="timeline-ticks">
          <div class="tick-ph"></div> <!-- Spacer for icon column -->
          <div class="tick-track">
            <div class="tick" v-for="t in getTicks()" :key="t.label" :style="{ left: t.percent + '%' }">
              <span class="tick-label">{{ t.label }}</span>
              <div class="tick-line"></div>
            </div>
          </div>
        </div>

        <div class="timeline-rows">
          <div v-if="detailedRows.length === 0" class="empty-state">
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
              <!-- Event blocks -->
              <div 
                v-for="(seg, idx) in row.usages" 
                :key="idx"
                class="row-segment"
                :class="{ hidden: seg.end < viewStart || seg.start > viewEnd }"
                :style="{
                  left: calculateLeft(seg.start) + '%',
                  width: calculateWidth(seg.start, seg.end) + '%'
                }"
                :title="`${formatTime(seg.start)} - ${formatTime(seg.end)}`"
              ></div>
            </div>
          </div>
        </div>
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
  background: #1a1a1a;
}

.row-segment {
  position: absolute;
  top: 10px;
  height: 20px;
  background: var(--accent);
  border-radius: 3px;
  opacity: 0.8;
  transition: opacity 0.2s;
  cursor: pointer;
}

.row-segment:hover {
  opacity: 1;
  z-index: 3;
}

.row-segment.hidden {
  display: none;
}

.empty-state {
  padding: 2rem;
  text-align: center;
  color: var(--text-dim);
  font-size: 0.8rem;
}
</style>
