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

// --- Main Timeline Drag State ---
const isDraggingTimeline = ref(false)
const timelineDragStartX = ref(0)
const timelineDragViewStart = ref(0)
const timelineDragViewEnd = ref(0)

// rAF throttle ID
let rafId = 0

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
  window.removeEventListener('mousemove', timelinePointerMove)
  window.removeEventListener('mouseup', timelinePointerUp)
  window.removeEventListener('touchmove', timelinePointerMove)
  window.removeEventListener('touchend', timelinePointerUp)
  window.removeEventListener('mousemove', minimapPointerMove)
  window.removeEventListener('mouseup', minimapPointerUp)
  window.removeEventListener('touchmove', minimapPointerMove)
  window.removeEventListener('touchend', minimapPointerUp)
  if (rafId) cancelAnimationFrame(rafId)
})

const handleResize = () => {
  if (timelineEl.value) containerWidth.value = timelineEl.value.clientWidth
}

// ========== Pre-parsed data (only recomputes when usageData changes, NOT on view pan) ==========

interface ParsedSegment {
  start: number
  end: number
}

// Parse timestamps once and group by appId
const parsedUsageByApp = computed(() => {
  const map = new Map<number, ParsedSegment[]>()
  
  for (const u of props.usageData) {
    if (!u.appId || !u.startTime || !u.endTime) continue
    let arr = map.get(u.appId)
    if (!arr) {
      arr = []
      map.set(u.appId, arr)
    }
    arr.push({
      start: new Date(u.startTime).getTime(),
      end: new Date(u.endTime).getTime()
    })
  }
  
  return map
})

// ========== View-dependent computeds (lightweight, only reads pre-parsed numbers) ==========

const activeAppsInView = computed(() => {
  const appDurations: [number, number][] = []
  const vs = viewStart.value
  const ve = viewEnd.value
  
  for (const [appId, segments] of parsedUsageByApp.value) {
    let totalDur = 0
    for (const seg of segments) {
      if (seg.end < vs || seg.start > ve) continue
      totalDur += Math.min(seg.end, ve) - Math.max(seg.start, vs)
    }
    if (totalDur > 0) {
      appDurations.push([appId, totalDur])
    }
  }
  
  appDurations.sort((a, b) => b[1] - a[1])
  return appDurations.map(d => d[0])
})

const detailedRows = computed(() => {
  const vs = viewStart.value
  const ve = viewEnd.value
  const totalRange = ve - vs
  if (totalRange <= 0) return []
  
  return activeAppsInView.value.map(appId => {
    const allSegments = parsedUsageByApp.value.get(appId) || []
    // Only include segments visible in the view, with pre-calculated positions
    const visibleUsages: { start: number, end: number, left: number, width: number, title: string }[] = []
    
    for (const seg of allSegments) {
      if (seg.end < vs || seg.start > ve) continue
      const l = Math.max(0, Math.min(100, ((seg.start - vs) / totalRange) * 100))
      const r = Math.max(0, Math.min(100, ((seg.end - vs) / totalRange) * 100))
      visibleUsages.push({
        start: seg.start,
        end: seg.end,
        left: l,
        width: Math.max(0.5, r - l),
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

// Lightweight time formatter (avoids creating full Date objects for display)
const fmtTime = (time: number) => {
  const d = new Date(time)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const ticks = computed(() => {
  const result: { percent: number, label: string }[] = []
  const range = viewEnd.value - viewStart.value
  if (range <= 0) return result
  
  const segments = 6
  for (let i = 0; i <= segments; i++) {
    const t = viewStart.value + (range / segments) * i
    result.push({
      percent: (i / segments) * 100,
      label: fmtTime(t)
    })
  }
  return result
})

// ========== Interaction handlers ==========

const applyViewUpdate = (newS: number, newE: number) => {
  const dayStart = new Date(props.selectedDate).setHours(0,0,0,0)
  const dayEnd = dayStart + 24 * 60 * 60 * 1000
  const range = newE - newS
  
  if (newS < dayStart) { newS = dayStart; newE = newS + range }
  if (newE > dayEnd) { newE = dayEnd; newS = newE - range }
  
  viewStart.value = newS
  viewEnd.value = newE
}

const handleWheel = (e: WheelEvent) => {
  const range = viewEnd.value - viewStart.value
  
  if (e.ctrlKey || e.metaKey) {
    e.preventDefault()
    const zoomFactor = e.deltaY > 0 ? 1.2 : 0.8
    const mouseX = e.offsetX
    const cw = timelineEl.value?.clientWidth || 800
    const pivotPercent = mouseX / cw
    const pivotTime = viewStart.value + range * pivotPercent
    
    let newStart = pivotTime - (pivotTime - viewStart.value) * zoomFactor
    let newEnd = pivotTime + (viewEnd.value - pivotTime) * zoomFactor
    
    const minRange = 5 * 60 * 1000
    const maxRange = 24 * 60 * 60 * 1000
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
    const shift = e.deltaX * (range / 1000)
    applyViewUpdate(viewStart.value + shift, viewEnd.value + shift)
  }
}

// Helper: extract clientX/clientY from mouse or touch event
const getClientX = (e: MouseEvent | TouchEvent): number => {
  return 'touches' in e ? e.touches[0].clientX : e.clientX
}
const getClientY = (e: MouseEvent | TouchEvent): number => {
  return 'touches' in e ? e.touches[0].clientY : e.clientY
}

// Track start Y for direction detection on touch
const pointerStartY = ref(0)
const touchDirectionLocked = ref<'h' | 'v' | null>(null)
const lastDragDeltaY = ref(0)

// Main timeline drag-to-pan (rAF throttled, mouse + touch)
const timelinePointerDown = (e: MouseEvent | TouchEvent) => {
  const target = ('touches' in e ? document.elementFromPoint(getClientX(e), getClientY(e)) : e.target) as HTMLElement | null
  if (target?.closest('.row-header')) return

  isDraggingTimeline.value = true
  timelineDragStartX.value = getClientX(e)
  timelineDragViewStart.value = viewStart.value
  timelineDragViewEnd.value = viewEnd.value
  pointerStartY.value = getClientY(e)
  touchDirectionLocked.value = null
  window.addEventListener('mousemove', timelinePointerMove)
  window.addEventListener('mouseup', timelinePointerUp)
  window.addEventListener('touchmove', timelinePointerMove, { passive: false })
  window.addEventListener('touchend', timelinePointerUp)
}

const timelinePointerMove = (e: MouseEvent | TouchEvent) => {
  if (!isDraggingTimeline.value) return

  // Detect direction on first significant move (works for both mouse and touch)
  if (!touchDirectionLocked.value) {
    const dx = Math.abs(getClientX(e) - timelineDragStartX.value)
    const dy = Math.abs(getClientY(e) - pointerStartY.value)
    if (dx + dy < 5) return // too small to determine
    touchDirectionLocked.value = dx >= dy ? 'h' : 'v'
  }

  // Vertical gesture: scroll the rows container
  if (touchDirectionLocked.value === 'v') {
    const rowsEl = timelineEl.value?.querySelector('.timeline-rows') as HTMLElement | null
    if (rowsEl) {
      const deltaY = getClientY(e) - pointerStartY.value
      rowsEl.scrollTop = rowsEl.scrollTop - (deltaY - (lastDragDeltaY.value || 0))
      lastDragDeltaY.value = deltaY
    }
    if ('touches' in e) e.preventDefault()
    return
  }

  if ('touches' in e) e.preventDefault()

  const clientX = getClientX(e)
  if (rafId) cancelAnimationFrame(rafId)
  rafId = requestAnimationFrame(() => {
    const deltaX = clientX - timelineDragStartX.value
    const trackWidth = (timelineEl.value?.clientWidth || 800) - 120
    const range = timelineDragViewEnd.value - timelineDragViewStart.value
    const timeDelta = -(deltaX / trackWidth) * range
    applyViewUpdate(timelineDragViewStart.value + timeDelta, timelineDragViewEnd.value + timeDelta)
  })
}

const timelinePointerUp = () => {
  isDraggingTimeline.value = false
  touchDirectionLocked.value = null
  lastDragDeltaY.value = 0
  window.removeEventListener('mousemove', timelinePointerMove)
  window.removeEventListener('mouseup', timelinePointerUp)
  window.removeEventListener('touchmove', timelinePointerMove)
  window.removeEventListener('touchend', timelinePointerUp)
}

// Minimap dragging (rAF throttled, mouse + touch)
const minimapPointerDown = (e: MouseEvent | TouchEvent, type: 'left' | 'right' | 'center') => {
  isDraggingMinimap.value = true
  dragType.value = type
  dragStartX.value = getClientX(e)
  dragStartViewStart.value = viewStart.value
  dragStartViewEnd.value = viewEnd.value
  window.addEventListener('mousemove', minimapPointerMove)
  window.addEventListener('mouseup', minimapPointerUp)
  window.addEventListener('touchmove', minimapPointerMove, { passive: false })
  window.addEventListener('touchend', minimapPointerUp)
}

const minimapPointerMove = (e: MouseEvent | TouchEvent) => {
  if (!isDraggingMinimap.value) return
  if ('touches' in e) e.preventDefault()
  const clientX = getClientX(e)
  if (rafId) cancelAnimationFrame(rafId)
  rafId = requestAnimationFrame(() => {
    const deltaX = clientX - dragStartX.value
    const minimapWidth = timelineEl.value?.clientWidth || 800
    const dayRange = 24 * 60 * 60 * 1000
    const timeDelta = (deltaX / minimapWidth) * dayRange
    
    const dayStart = new Date(props.selectedDate).setHours(0,0,0,0)
    const dayEnd = dayStart + dayRange
    const minRange = 5 * 60 * 1000
    
    if (dragType.value === 'center') {
      applyViewUpdate(dragStartViewStart.value + timeDelta, dragStartViewEnd.value + timeDelta)
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
  })
}

const minimapPointerUp = () => {
  isDraggingMinimap.value = false
  dragType.value = null
  window.removeEventListener('mousemove', minimapPointerMove)
  window.removeEventListener('mouseup', minimapPointerUp)
  window.removeEventListener('touchmove', minimapPointerMove)
  window.removeEventListener('touchend', minimapPointerUp)
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

const minimapActivities = computed(() => {
  const day = 24 * 60 * 60 * 1000
  const dayS = dayStartMs.value
  
  const intervals: {start: number, end: number}[] = []
  
  // Use pre-parsed data instead of creating new Date objects
  const rawIntervals: {start: number, end: number}[] = []
  for (const segments of parsedUsageByApp.value.values()) {
    for (const seg of segments) {
      rawIntervals.push(seg)
    }
  }
  rawIntervals.sort((a, b) => a.start - b.start)
    
  if (rawIntervals.length === 0) return []
  
  let current = { ...rawIntervals[0] }
  for (let i = 1; i < rawIntervals.length; i++) {
    const next = rawIntervals[i]
    if (next.start <= current.end + 60000) {
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
