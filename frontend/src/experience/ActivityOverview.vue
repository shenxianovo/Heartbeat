<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { groupObjects, presentFact, rangeOf, type ExperienceSegment, type TimeRange } from './factViews'
import { dragOverview, type OverviewDrag } from './overviewRange'
import { niceTicks } from '../timeline/timeScale'

const props = defineProps<{ facts: ExperienceSegment[]; range: TimeRange; bounds: TimeRange; timeZone: string }>()
const emit = defineEmits<{ range: [value: TimeRange] }>()
const track = ref<HTMLElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)
let observer: ResizeObserver | undefined
let drag: { mode: OverviewDrag; range: TimeRange; anchor: number; x: number; moved: boolean } | null = null
const groups = computed(() => groupObjects(props.facts, props.bounds))
const height = computed(() => Math.max(36, groups.value.length * 10 + 12))
const percent = (time: number) => (time - props.bounds.start) / (props.bounds.end - props.bounds.start) * 100
const left = computed(() => percent(props.range.start))
const right = computed(() => percent(props.range.end))
const isFull = computed(() => props.range.start === props.bounds.start && props.range.end === props.bounds.end)
const ticks = computed(() => niceTicks(props.bounds.start, props.bounds.end, 5, props.timeZone))
const format = (time: number) => (time === props.bounds.end ? '次日 ' : '') + new Date(time).toLocaleTimeString('zh-CN', {
  hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit', timeZone: props.timeZone,
})
function draw() {
  const el = canvas.value
  if (!el) return
  const width = el.clientWidth, scale = devicePixelRatio || 1
  el.width = Math.round(width * scale); el.height = Math.round(height.value * scale)
  const ctx = el.getContext('2d')
  if (!ctx) return
  ctx.scale(scale, scale)
  for (const [row, group] of groups.value.entries()) {
    for (const fact of group.facts) {
      const raw = rangeOf(fact)
      const start = Math.max(0, percent(raw.start) / 100 * width)
      const end = Math.min(width, percent(raw.end) / 100 * width)
      ctx.fillStyle = presentFact(fact).color
      ctx.globalAlpha = .65
      ctx.fillRect(start, 7 + row * 10, Math.max(.7, end - start), 6)
    }
  }
}
function timeAt(x: number) {
  const rect = track.value!.getBoundingClientRect()
  return props.bounds.start + Math.max(0, Math.min(1, (x - rect.left) / rect.width)) * (props.bounds.end - props.bounds.start)
}
function down(event: PointerEvent) {
  if (event.button !== 0) return
  event.preventDefault()
  const target = (event.target as HTMLElement).closest<HTMLElement>('[data-drag]')
  let mode = (target?.dataset.drag || 'select') as OverviewDrag
  if (event.shiftKey || (mode === 'move' && isFull.value)) mode = 'select'
  drag = { mode, range: { ...props.range }, anchor: timeAt(event.clientX), x: event.clientX, moved: false }
  track.value!.setPointerCapture(event.pointerId)
}
function move(event: PointerEvent) {
  if (!drag) return
  if (Math.abs(event.clientX - drag.x) > 3) drag.moved = true
  if (drag.moved) emit('range', dragOverview(drag.mode, drag.range, props.bounds, drag.anchor, timeAt(event.clientX)))
}
function up(event: PointerEvent) {
  if (drag && !drag.moved && drag.mode === 'select' && !isFull.value) {
    const center = (drag.range.start + drag.range.end) / 2
    emit('range', dragOverview('move', drag.range, props.bounds, center, timeAt(event.clientX)))
  }
  drag = null
}
function key(event: KeyboardEvent, mode: OverviewDrag) {
  if (event.key === 'Escape') { event.preventDefault(); emit('range', props.bounds); return }
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const anchor = mode === 'end' ? props.range.end : props.range.start
  const step = (props.range.end - props.range.start) * (event.shiftKey ? .1 : .02)
  const current = event.key === 'Home' ? props.bounds.start : event.key === 'End' ? props.bounds.end
    : anchor + Math.max(1000, step) * (event.key === 'ArrowLeft' ? -1 : 1)
  emit('range', dragOverview(mode, props.range, props.bounds, anchor, current))
}
watch([groups, height], draw, { flush: 'post' })
onMounted(() => { observer = new ResizeObserver(draw); observer.observe(canvas.value!); draw() })
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <div class="activity-overview">
    <div class="overview-heading"><span>全天</span><span class="window-label">{{ format(range.start) }} — {{ format(range.end) }}</span></div>
    <div ref="track" class="overview-track" :style="{ height: `${height}px` }"
      @pointerdown="down" @pointermove="move" @pointerup="up" @pointercancel="drag = null"
      @dblclick="$emit('range', bounds)">
      <canvas ref="canvas" :style="{ height: `${height}px` }" aria-hidden="true" />
      <div class="shade" :style="{ left: 0, width: `${left}%` }" />
      <div class="shade" :style="{ left: `${right}%`, right: 0 }" />
      <div class="overview-selection" :class="{ full: isFull }" :style="{ left: `${left}%`, width: `${right - left}%` }" data-drag="move">
        <div class="overview-move" tabindex="0" role="slider" aria-label="移动时间范围" :aria-valuemin="bounds.start" :aria-valuemax="bounds.end - (range.end - range.start)"
          :aria-valuenow="range.start" :aria-valuetext="`${format(range.start)} — ${format(range.end)}`" @keydown.stop="key($event, 'move')" />
        <div class="overview-handle start" data-drag="start" tabindex="0" role="slider" aria-label="范围起点"
          :aria-valuemin="bounds.start" :aria-valuemax="range.end - 1000" :aria-valuenow="range.start" :aria-valuetext="format(range.start)" @keydown.stop="key($event, 'start')" />
        <div class="overview-handle end" data-drag="end" tabindex="0" role="slider" aria-label="范围终点"
          :aria-valuemin="range.start + 1000" :aria-valuemax="bounds.end" :aria-valuenow="range.end" :aria-valuetext="format(range.end)" @keydown.stop="key($event, 'end')" />
      </div>
    </div>
    <div class="overview-ticks"><span v-for="tick in ticks" :key="tick.at" :style="{ left: `${tick.percent}%` }">{{ tick.at === bounds.end ? '次日 ' : '' }}{{ tick.label }}</span></div>
  </div>
</template>

<style scoped>
.overview-heading { display: flex; justify-content: space-between; gap: 12px; margin-bottom: 10px; font-size: .7rem; color: var(--muted-foreground); }
.window-label { font-variant-numeric: tabular-nums; }
.overview-track { position: relative; background: var(--muted); border-radius: 5px; cursor: crosshair; touch-action: none; user-select: none; }
canvas { display: block; width: 100%; border-radius: inherit; }
.shade { position: absolute; top: 0; bottom: 0; background: color-mix(in srgb, var(--background) 65%, transparent); pointer-events: none; }
.overview-selection { position: absolute; top: 0; bottom: 0; border: 1px solid var(--primary); background: var(--primary-soft); cursor: grab; }
.overview-selection.full { cursor: crosshair; }.overview-selection:active { cursor: grabbing; }
.overview-move { position: absolute; inset: 0; }
.overview-handle { position: absolute; top: -2px; bottom: -2px; width: 12px; cursor: ew-resize; z-index: 2; }
.overview-handle::after { content: ''; position: absolute; top: 0; bottom: 0; width: 5px; background: var(--primary); border-radius: 3px; }
.overview-handle.start { left: -12px; }.overview-handle.start::after { right: 0; }
.overview-handle.end { right: -12px; }.overview-handle.end::after { left: 0; }
[tabindex]:focus-visible { outline: 2px solid var(--primary); outline-offset: 3px; }
.overview-ticks { height: 24px; position: relative; margin-top: 6px; font-size: .65rem; color: var(--muted-foreground); font-variant-numeric: tabular-nums; }
.overview-ticks span { position: absolute; transform: translateX(-50%); white-space: nowrap; }
.overview-ticks span:first-child { transform: none; }.overview-ticks span:last-child { transform: translateX(-100%); }
</style>
