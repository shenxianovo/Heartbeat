<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { Monitor, UserRound, Globe, ChevronRight, AppWindow } from 'lucide-vue-next'
import AppIcon from '../components/AppIcon.vue'
import { useTimelineDrag } from '../composables/useTimelineDrag'
import { niceTicks } from '../timeline/timeScale'
import ActivityOverview from './ActivityOverview.vue'
import FactLane from './FactLane.vue'
import FactCard from './FactCard.vue'
import { groupApplications, groupSubjects, hasFactView, type ExperienceSegment, type TimeRange } from './factViews'

const props = defineProps<{
  username: string; facts: ExperienceSegment[]; range: TimeRange; bounds: TimeRange; timeZone: string
  selectedId?: string
}>()
const emit = defineEmits<{ range: [range: TimeRange]; select: [fact: ExperienceSegment]; focus: [fact: ExperienceSegment] }>()
const expandedDevices = ref(new Set<string>())
const expandedBrowser = ref(new Set<string>())
const subjects = computed(() => groupSubjects(props.facts, props.range))
const timeline = ref<HTMLElement | null>(null)
const axis = ref<HTMLElement | null>(null)
const trackWidth = ref(700)
const scrollbarWidth = ref(0)
const viewStart = ref(props.range.start)
const viewEnd = ref(props.range.end)
watch(() => props.range, range => { viewStart.value = range.start; viewEnd.value = range.end })
watch([viewStart, viewEnd], ([start, end]) => {
  if (start !== props.range.start || end !== props.range.end) emit('range', { start, end })
})
const { timelinePointerDown, handleWheel, didDrag } = useTimelineDrag(viewStart, viewEnd, timeline, computed(() => props.bounds), {
  trackRect: () => axis.value?.getBoundingClientRect(), minRangeMs: 1000,
})
function suppressDragClick(event: MouseEvent) { if (didDrag.value && event.detail > 0) { event.stopPropagation(); event.preventDefault() } }
const ticks = computed(() => {
  const { start, end } = props.range
  const count = Math.max(2, Math.floor(trackWidth.value / 80))
  if (end - start >= 120_000) return niceTicks(start, end, count, props.timeZone)
  const step = [1000, 2000, 5000, 10_000, 15_000, 30_000, 60_000].find(step => (end - start) / step <= count) ?? 60_000
  const result = []
  for (let at = Math.ceil(start / step) * step; at <= end; at += step) result.push({ at, percent: (at - start) / (end - start) * 100,
    label: new Date(at).toLocaleTimeString('en-GB', { timeZone: props.timeZone, hour12: false }) })
  return result
})
let observer: ResizeObserver | undefined
onMounted(() => {
  observer = new ResizeObserver(() => {
    const rows = timeline.value?.querySelector('.timeline-rows') as HTMLElement | null
    scrollbarWidth.value = rows ? rows.offsetWidth - rows.clientWidth : 0
    trackWidth.value = axis.value?.clientWidth || 700
  })
  observer.observe(axis.value!)
  observer.observe(timeline.value!.querySelector('.timeline-rows')!)
})
onUnmounted(() => observer?.disconnect())
function toggle(set: Set<string>, id: string) { if (set.has(id)) set.delete(id); else set.add(id) }
function sources(facts: ExperienceSegment[]) {
  const order = (source: string) => source === 'system' ? 0 : source === 'browser' ? 1 : 2
  return [...new Set(facts.map(f => f.source))].sort((a, b) => order(a) - order(b) || a.localeCompare(b))
}
const sourceFacts = (facts: ExperienceSegment[], source: string) => facts.filter(f => f.source === source)
const hasSystem = (facts: ExperienceSegment[]) => facts.some(f => f.source === 'system')
const sourceLabel = (source: string) => source === 'system' ? '前台活动' : source === 'browser' ? 'Browser' : source === 'vrchat.account' ? 'VRChat' : source
</script>

<template>
  <div class="activity-swimlanes" :style="{ '--scrollbar-width': `${scrollbarWidth}px` }">
    <ActivityOverview :facts="facts" :range="range" :bounds="bounds" :time-zone="timeZone" @range="emit('range', $event)" />
    <div ref="timeline" class="timeline" @mousedown="timelinePointerDown" @touchstart.passive="timelinePointerDown" @wheel="handleWheel" @click.capture="suppressDragClick">
      <div ref="axis" class="axis"><span v-for="tick in ticks" :key="tick.at" :style="{ left: `${tick.percent}%` }">{{ tick.label }}</span></div>
      <div class="timeline-rows">
        <div v-if="!subjects.length && facts.length" class="empty-range">这个范围内没有活动</div>
        <section v-for="subject in subjects" :key="subject.id" class="subject">
          <template v-for="(source, index) in sources(subject.facts)" :key="source">
            <template v-if="source !== 'browser' || !hasSystem(subject.facts) || expandedBrowser.has(subject.id)">
              <div class="lane-row" :class="{ 'subject-summary': index === 0 }">
                <div v-if="index === 0" class="subject-label row-header">
                  <button v-if="subject.kind === 'machine' && hasSystem(subject.facts)" class="subject-name device-toggle" :aria-expanded="expandedDevices.has(subject.id)" :aria-label="`${expandedDevices.has(subject.id) ? '收起' : '展开'} ${subject.name} 的应用`" @click="toggle(expandedDevices, subject.id)">
                    <ChevronRight :size="12" class="chevron" :class="{ open: expandedDevices.has(subject.id) }" /><Monitor :size="17" /><strong :title="subject.name">{{ subject.name }}</strong>
                  </button>
                  <div v-else class="subject-name"><Monitor v-if="subject.kind === 'machine'" :size="17" /><UserRound v-else :size="17" /><strong :title="subject.name">{{ subject.name }}</strong></div>
                  <span>{{ sourceLabel(source) }}</span>
                </div>
                <div v-else class="lane-label row-header"><Globe v-if="source === 'browser'" :size="14" />{{ sourceLabel(source) }}</div>
                <FactLane :facts="sourceFacts(subject.facts, source)" :range="range" :bounds="bounds" :ticks="ticks" :time-zone="timeZone" :selected-id="selectedId" :label="`${subject.name} ${source}`" @range="emit('range', $event)" @select="emit('select', $event)" />
              </div>
              <template v-if="source === 'system' && expandedDevices.has(subject.id)">
                <div v-for="app in groupApplications(subject.facts)" :key="app.id" class="lane-row app-row">
                  <div class="app-label row-header" :title="app.name"><span class="app-icon"><AppWindow :size="17" /><AppIcon v-if="app.appId != null" :username="username" :app-id="app.appId" /></span><span>{{ app.name }}</span></div>
                  <FactLane compact :facts="app.facts" :range="range" :bounds="bounds" :ticks="ticks" :time-zone="timeZone" :selected-id="selectedId" :label="`${subject.name} · ${app.name}`" @range="emit('range', $event)" @select="emit('select', $event)" />
                </div>
              </template>
              <details v-if="!hasFactView(source)" class="unknown-source row-header"><summary>{{ sourceFacts(subject.facts, source).length }} 条观察 · Payload 样例</summary>
                <FactCard v-for="fact in sourceFacts(subject.facts, source).slice(0, 3)" :key="fact.id" :fact="fact" :time-zone="timeZone" @select="emit('select', $event)" @focus="emit('focus', $event)" />
              </details>
            </template>
          </template>
          <button v-if="subject.facts.some(f => f.source === 'browser') && hasSystem(subject.facts)" class="browser-toggle row-header" :aria-expanded="expandedBrowser.has(subject.id)" @click="toggle(expandedBrowser, subject.id)"><span aria-hidden="true">{{ expandedBrowser.has(subject.id) ? '⌄' : '›' }}</span> Browser <span>{{ sourceFacts(subject.facts, 'browser').length }}</span></button>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.activity-swimlanes { --label-width: 164px; --lane-gap: 16px; }
.timeline { user-select: none; cursor: grab; }.timeline:active { cursor: grabbing; }
.axis { position: relative; height: 30px; margin: 18px 0 0 calc(var(--label-width) + var(--lane-gap)); font-size: .65rem; color: var(--muted-foreground); font-variant-numeric: tabular-nums; }
.axis span { position: absolute; transform: translateX(-50%); white-space: nowrap; }.axis span:first-child { transform: none; }.axis span:last-child { transform: translateX(-100%); }
.timeline-rows { max-height: 440px; overflow-y: auto; scrollbar-gutter: stable; }
/* Keep the ruler and rows on exactly the same track width, including classic scrollbars. */
.axis { margin-right: var(--scrollbar-width, 0px); }
.subject { padding: 10px 0; border-top: 1px solid var(--glass-border); }
.lane-row { display: grid; grid-template-columns: var(--label-width) minmax(0, 1fr); gap: var(--lane-gap); align-items: start; }
.subject-summary { position: sticky; top: 0; z-index: 1; background: var(--background); border-radius: 6px; }
.subject-label { min-width: 0; padding-top: 11px; }.subject-name { display: flex; align-items: center; gap: 7px; width: 100%; text-align: left; }
.subject-name svg { color: var(--muted-foreground); flex-shrink: 0; }.subject-name strong { font-weight: 550; font-size: .85rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.device-toggle { cursor: pointer; }.chevron { transition: transform .15s; }.chevron.open { transform: rotate(90deg); }
.subject-label > span { display: block; font-size: .65rem; color: var(--muted-foreground); margin: 4px 0 0 26px; }
.app-label { display: flex; align-items: center; gap: 8px; padding: 10px 0 0 20px; min-width: 0; font-size: .75rem; }
.app-label > span:last-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }.app-icon { position: relative; width: 18px; height: 18px; flex-shrink: 0; color: var(--muted-foreground); }.app-icon :deep(img) { position: absolute; inset: 0; width: 18px; height: 18px; object-fit: contain; background: var(--background); border-radius: 4px; }
.lane-label { display: flex; align-items: center; gap: 7px; padding: 15px 0 0 26px; color: var(--muted-foreground); font-size: .75rem; overflow-wrap: anywhere; }
.browser-toggle { margin: 5px 0 0 26px; display: flex; gap: 7px; align-items: center; color: var(--muted-foreground); font-size: .7rem; cursor: pointer; }
button:hover { color: var(--primary); }button:focus-visible { outline: 2px solid var(--primary); outline-offset: 4px; }
.unknown-source { margin: 8px 0 12px calc(var(--label-width) + var(--lane-gap)); font-size: .75rem; color: var(--muted-foreground); }.unknown-source summary { cursor: pointer; }.unknown-source article { margin: 10px 0; }
.empty-range { padding: 32px 12px; text-align: center; font-size: .8rem; color: var(--muted-foreground); }
@media (max-width: 680px) { .activity-swimlanes { --label-width: 110px; --lane-gap: 8px; }.subject-name { gap: 4px; }.subject-name strong { font-size: .75rem; }.axis { font-size: .6rem; }.unknown-source { margin-left: 0; }.subject-label > span { margin-left: 23px; }.app-label { padding-left: 12px; gap: 6px; } }
</style>
