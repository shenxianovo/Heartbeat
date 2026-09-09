<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { clampRange, overlaps, presentFact, rangeOf, zoomRange, type ExperienceSegment, type TimeRange } from './factViews'
const props = defineProps<{
  facts: ExperienceSegment[]; range: TimeRange; bounds: TimeRange; selectedId?: string; label: string
  compact?: boolean; ticks: { percent: number }[]; timeZone: string
}>()
const emit = defineEmits<{ range: [range: TimeRange]; select: [fact: ExperienceSegment] }>()
const canvas = ref<HTMLCanvasElement | null>(null)
const brush = ref<{ start: number; end: number } | null>(null)
let observer: ResizeObserver | undefined
let drag: { x: number; range: TimeRange; moved: boolean } | null = null
let skipClick = false
const packed = computed(() => {
  const ends: number[] = []
  return props.facts.filter(f => overlaps(rangeOf(f), props.range))
    .sort((a, b) => Date.parse(a.startTime) - Date.parse(b.startTime) || a.id.localeCompare(b.id))
    .map(fact => {
      const time = rangeOf(fact)
      let row = ends.findIndex(end => end <= time.start)
      if (row < 0) row = ends.length
      ends[row] = time.end
      return { fact, time, row }
    })
})
const height = computed(() => Math.max(props.compact ? 40 : 56, (packed.value.reduce((max, p) => Math.max(max, p.row), 0) + 1) * 28 + 16))
function position(time: number, width: number) { return (time - props.range.start) / (props.range.end - props.range.start) * width }
function draw() {
  const el = canvas.value
  if (!el) return
  const width = el.clientWidth
  const scale = window.devicePixelRatio || 1
  el.width = Math.round(width * scale); el.height = Math.round(height.value * scale)
  const ctx = el.getContext('2d')
  if (!ctx) return
  ctx.scale(scale, scale)
  ctx.clearRect(0, 0, width, height.value)
  ctx.strokeStyle = 'rgba(128,145,160,.15)'
  for (const tick of props.ticks) { const x = width * tick.percent / 100; ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height.value); ctx.stroke() }
  for (const { fact, time, row } of packed.value) {
    const x = Math.max(0, position(time.start, width))
    const end = Math.min(width, position(time.end, width))
    const y = row * 28 + 12
    const view = presentFact(fact)
    ctx.fillStyle = view.color
    ctx.globalAlpha = props.selectedId && props.selectedId !== fact.id ? .48 : .85
    // Subpixel observations remain individual marks, never merged into a synthetic interval.
    ctx.fillRect(x, y, Math.max(0.7, end - x), 20)
    ctx.globalAlpha = 1
    if (end - x > 3) { ctx.strokeStyle = 'rgba(255,255,255,.45)'; ctx.strokeRect(x + .5, y + .5, end - x - 1, 19) }
    if (fact.id === props.selectedId) { ctx.strokeStyle = view.color; ctx.lineWidth = 2; ctx.strokeRect(x - 2, y - 3, Math.max(1, end - x) + 4, 26); ctx.lineWidth = 1 }
    if (end - x > 80) {
      ctx.save(); ctx.beginPath(); ctx.rect(x + 5, y, end - x - 10, 20); ctx.clip()
      ctx.fillStyle = '#fff'; ctx.font = '11px system-ui'; ctx.fillText(view.title, x + 7, y + 14); ctx.restore()
    }
  }
  if (brush.value) {
    ctx.fillStyle = 'rgba(56,139,181,.2)'
    ctx.fillRect(Math.min(brush.value.start, brush.value.end), 0, Math.abs(brush.value.end - brush.value.start), height.value)
  }
}
function xOf(event: MouseEvent) { return event.clientX - canvas.value!.getBoundingClientRect().left }
function down(event: PointerEvent) {
  skipClick = false
  if (event.button !== 0 || !event.shiftKey) return
  canvas.value!.setPointerCapture(event.pointerId)
  drag = { x: xOf(event), range: { ...props.range }, moved: false }
}
function hitAt(event: MouseEvent) {
  const width = canvas.value!.clientWidth
  const y = event.clientY - canvas.value!.getBoundingClientRect().top
  return packed.value.find(p => y >= p.row * 28 + 12 && y <= p.row * 28 + 32 &&
    xOf(event) >= position(p.time.start, width) - 2 && xOf(event) <= position(p.time.end, width) + 2)
}
function move(event: PointerEvent) {
  if (!drag) {
    const hit = hitAt(event)
    const time = (value: string) => new Date(value).toLocaleTimeString('en-GB', { timeZone: props.timeZone, hour12: false })
    canvas.value!.title = hit ? `${presentFact(hit.fact).title} · ${time(hit.fact.startTime)} – ${time(hit.fact.endTime)}` : ''
    return
  }
  const x = xOf(event)
  if (Math.abs(x - drag.x) > 4) drag.moved = true
  if (drag.moved) brush.value = { start: drag.x, end: x }
}
function up(event: PointerEvent) {
  if (!drag) return
  if (drag.moved) {
    const a = drag.range.start + drag.x / canvas.value!.clientWidth * (drag.range.end - drag.range.start)
    const b = drag.range.start + xOf(event) / canvas.value!.clientWidth * (drag.range.end - drag.range.start)
    emit('range', clampRange({ start: Math.min(a, b), end: Math.max(a, b) }, props.bounds))
    skipClick = true
  }
  drag = null; brush.value = null
}
function select(event: MouseEvent) {
  if (skipClick) { skipClick = false; return }
  const hit = hitAt(event)
  if (hit) emit('select', hit.fact)
}
function key(event: KeyboardEvent) {
  if (['+', '=', '-'].includes(event.key)) {
    event.preventDefault(); emit('range', zoomRange(props.range, props.bounds, event.key === '-' ? 1.5 : 1 / 1.5))
  } else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
    event.preventDefault()
    const shift = (props.range.end - props.range.start) * (event.key === 'ArrowLeft' ? -.2 : .2)
    emit('range', clampRange({ start: props.range.start + shift, end: props.range.end + shift }, props.bounds))
  }
}
watch([packed, () => props.selectedId, () => props.range, () => props.ticks, brush], draw, { flush: 'post' })
onMounted(() => { observer = new ResizeObserver(draw); observer.observe(canvas.value!); draw() })
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <div class="lane-scroll">
    <canvas ref="canvas" :style="{ height: `${height}px` }" tabindex="0" role="img"
      :aria-label="`${label}，${packed.length} 条记录。方向键平移，加减键缩放；逐条内容见下方记录列表。`"
      @pointerdown="down" @pointermove="move" @pointerup="up" @pointercancel="drag = null; brush = null"
      @click="select" @keydown="key" />
  </div>
</template>

<style scoped>
.lane-scroll { min-width: 0; }
canvas { display: block; width: 100%; cursor: grab; touch-action: none; border-radius: 7px; }
canvas:active { cursor: grabbing; }
canvas:focus-visible { outline: 2px solid var(--primary); outline-offset: -2px; }
</style>
