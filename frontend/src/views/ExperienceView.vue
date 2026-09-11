<script setup lang="ts">
import { computed, onUnmounted, ref, shallowRef, watch } from 'vue'
import { useRoute } from 'vue-router'
import { initialViewBounds } from '../timeline/timelineModel'
import DatePicker from '../components/DatePicker.vue'
import { fetchExperiencePage } from '../api'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import ActivitySwimlanes from '../experience/ActivitySwimlanes.vue'
import FactCard from '../experience/FactCard.vue'
import { clampRange, groupObjects, factObject, overlaps, rangeOf, relatedPages,
  type ExperienceSegment, type TimeRange } from '../experience/factViews'

const route = useRoute()
const username = computed(() => String(route.params.username))
const now = new Date()
const date = ref(`${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`)
const calendar = computed(() => resolveCalendarContext(date.value))
const bounds = computed(() => ({ start: Date.parse(calendar.value.day.start), end: Date.parse(calendar.value.day.endExclusive) }))
const range = ref<TimeRange>(bounds.value)
const facts = shallowRef<ExperienceSegment[]>([])
const loading = ref(false)
const error = ref('')
const selected = ref<ExperienceSegment | null>(null)
const targetFilter = ref('')
const page = ref(0)
const relatedLimit = ref(4)
let controller: AbortController | undefined
let viewEstablished = false
function initialRange() {
  const timestamp = Date.now()
  const today = timestamp >= bounds.value.start && timestamp < bounds.value.end
  const first = facts.value.reduce<number | undefined>((earliest, fact) =>
    Math.min(earliest ?? Infinity, Date.parse(fact.startTime)), undefined)
  return initialViewBounds(bounds.value, today, first === undefined ? [] : [{ startTime: new Date(first) }], timestamp)
}

async function refresh() {
  controller?.abort()
  const request = new AbortController(); controller = request
  const capturedWindow = calendar.value.day
  const capturedUser = username.value
  facts.value = []; selected.value = null; error.value = ''; loading.value = true; page.value = 0
  try {
    let after: string | null = null
    do {
      const result = await fetchExperiencePage(capturedUser, capturedWindow, after, request.signal)
      if (request.signal.aborted) return
      facts.value = [...facts.value, ...result.items]
      after = result.nextCursor
    } while (after)
    if (!viewEstablished) { range.value = initialRange(); viewEstablished = true }
  } catch (cause) {
    if (!request.signal.aborted) error.value = cause instanceof Error ? cause.message : '加载失败，请重试。'
  } finally {
    if (!request.signal.aborted) loading.value = false
  }
}
watch([date, username], () => {
  viewEstablished = false; facts.value = []; range.value = initialRange(); targetFilter.value = ''; void refresh()
}, { immediate: true })
onUnmounted(() => controller?.abort())

const allTargets = computed(() => groupObjects(facts.value, bounds.value))
const related = computed(() => selected.value ? relatedPages(selected.value, facts.value) : [])
const visibleFacts = computed(() => facts.value.filter(f => overlaps(rangeOf(f), range.value) &&
  (!targetFilter.value || factObject(f).id === targetFilter.value))
  .sort((a, b) => Date.parse(a.startTime) - Date.parse(b.startTime) || a.id.localeCompare(b.id)))
const pageCount = computed(() => Math.ceil(visibleFacts.value.length / 8))
const pageFacts = computed(() => visibleFacts.value.slice(page.value * 8, page.value * 8 + 8))
watch([range, targetFilter], () => {
  page.value = 0
  if (selected.value && (!overlaps(rangeOf(selected.value), range.value) ||
    (targetFilter.value && factObject(selected.value).id !== targetFilter.value))) selected.value = null
}, { deep: true })
watch(selected, () => { relatedLimit.value = 4 })

function setRange(value: TimeRange) { viewEstablished = true; range.value = clampRange(value, bounds.value) }
function choose(fact: ExperienceSegment) {
  if (targetFilter.value && targetFilter.value !== factObject(fact).id) targetFilter.value = ''
  selected.value = fact
  const index = visibleFacts.value.findIndex(f => f.id === fact.id)
  if (index >= 0) page.value = Math.floor(index / 8)
}
function focusFact(fact: ExperienceSegment) {
  const raw = rangeOf(fact)
  const pad = Math.max(1000, (raw.end - raw.start) * .2)
  setRange({ start: raw.start - pad, end: raw.end + pad })
  selected.value = fact
}
</script>

<template>
  <main class="experience">
    <header class="experience-header">
      <div>
        <RouterLink class="back" :to="`/u/${encodeURIComponent(username)}`">← {{ username }} 的看板</RouterLink>
        <h1>当天经历<span class="preview">预览</span></h1>
      </div>
      <div class="date-tools"><DatePicker v-model="date" /><button class="control" :disabled="loading" @click="refresh">刷新</button></div>
    </header>

    <section class="glass-panel experience-panel" aria-label="当天活动">
      <div class="toolbar">
        <h2>活动泳道</h2>
      </div>
      <div v-if="error" class="notice error" role="alert">{{ error }}<span v-if="facts.length"> 当前只加载了 {{ facts.length }} 条，结果不完整。</span><button @click="refresh">重新加载</button></div>
      <div v-if="loading" class="notice" role="status">正在读取当天记录…<span v-if="facts.length"> 已加载 {{ facts.length }} 条，尚未完成。</span></div>
      <div v-if="!loading && !error && !facts.length" class="empty">这一天还没有活动记录。<small>可以选择其他日期，或回到看板查看采集状态。</small></div>
      <ActivitySwimlanes :key="`${username}/${date}`" :username="username" :facts="facts" :range="range" :bounds="bounds" :time-zone="calendar.day.timeZone" :selected-id="selected?.id" @range="setRange" @select="choose" @focus="focusFact" />
    </section>

    <section v-if="facts.length" class="records-section">
      <div v-if="selected" class="selection glass-panel">
        <div class="section-heading"><h2>选中的记录</h2><button class="control" @click="selected = null">取消选择</button></div>
        <FactCard :fact="selected" selected :time-zone="calendar.day.timeZone" @select="choose" @focus="focusFact" />
        <template v-if="selected.aspect === 'desktop-activity'">
          <h3>相关 Browser 观察 <span>{{ related.length }}</span></h3>
          <p v-if="!related.length" class="hint">暂无相关观察</p>
          <div class="card-grid"><FactCard v-for="fact in related.slice(0, relatedLimit)" :key="fact.id" :fact="fact" :time-zone="calendar.day.timeZone" @select="choose" @focus="focusFact" /></div>
          <button v-if="related.length > relatedLimit" class="control" @click="relatedLimit += 8">继续查看（还剩 {{ related.length - relatedLimit }} 条）</button>
        </template>
      </div>
      <div class="section-heading">
        <div><h2>记录 <span class="record-count">{{ visibleFacts.length }}</span></h2><p v-if="loading || error" class="hint">结果尚不完整</p></div>
        <select v-model="targetFilter" aria-label="筛选对象" class="control"><option value="">全部对象</option><option v-for="target in allTargets" :key="target.id" :value="target.id">{{ target.name }}</option></select>
      </div>
      <div class="card-grid"><div v-for="fact in pageFacts" :key="fact.id"><p class="record-target">{{ factObject(fact).name }} · {{ fact.source ?? '未知来源' }}</p><FactCard :fact="fact" :selected="selected?.id === fact.id" :time-zone="calendar.day.timeZone" @select="choose" @focus="focusFact" /></div></div>
      <p v-if="!visibleFacts.length" class="hint">所选对象在这个范围内没有记录。</p>
      <nav v-if="pageCount > 1" class="pagination" aria-label="记录分页"><button class="control" :disabled="page === 0" @click="page--">上一页</button><span>{{ page + 1 }} / {{ pageCount }}</span><button class="control" :disabled="page + 1 >= pageCount" @click="page++">下一页</button></nav>
    </section>
    <footer>{{ calendar.day.timeZone }}</footer>
  </main>
</template>

<style scoped>
.experience { position: relative; z-index: 1; max-width: 1280px; margin: 0 auto; padding: 42px 32px 120px; }
.experience-header { display: flex; justify-content: space-between; align-items: center; gap: 24px; margin-bottom: 32px; padding-right: 36px; }
.back { font-size: .8rem; color: var(--muted-foreground); text-decoration: none; }
h1 { font-size: 2rem; font-weight: 650; letter-spacing: -.04em; margin-top: 10px; display: flex; align-items: center; gap: 14px; }
.preview { font-size: .65rem; letter-spacing: .05em; font-weight: 500; border: 1px solid var(--glass-border); padding: 3px 9px; border-radius: 20px; color: var(--muted-foreground); }
.date-tools, .toolbar, .section-heading { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.experience-panel { padding: 24px; border-radius: 20px; }
.glass-panel { background: var(--glass-bg); border: 1px solid var(--glass-border); backdrop-filter: blur(var(--glass-blur)); box-shadow: 0 4px 24px #00000003; }
.control { cursor: pointer; border-radius: 8px; padding: 7px 12px; font-size: .8rem; border: 1px solid var(--glass-border); background: var(--glass-bg); }
button:disabled { opacity: .4; cursor: default; }
button:focus-visible, select:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
.toolbar { margin-bottom: 20px; }
.hint { color: var(--muted-foreground); font-size: .75rem; line-height: 1.7; margin: 10px 0; }
.record-count { margin-left: 6px; color: var(--muted-foreground); font-size: .85rem; font-weight: 400; }
.notice { padding: 15px; margin: 16px 0; border-radius: 10px; background: var(--muted); font-size: .85rem; }
.notice button { margin-left: 12px; color: var(--primary); cursor: pointer; }.error { color: var(--destructive); }
.empty { padding: 65px 12px; text-align: center; color: var(--muted-foreground); display: grid; justify-items: center; gap: 15px; }.empty small { font-size: .8rem; }
.records-section { margin-top: 34px; }.section-heading { justify-content: space-between; margin-bottom: 18px; }h2 { font-size: 1.05rem; font-weight: 600; }h3 { font-size: .85rem; margin: 22px 0 0; }h3 span { color: var(--muted-foreground); }
.selection { border-radius: 16px; padding: 22px; margin-bottom: 30px; }.selection > .control { margin-top: 12px; }
.card-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }.record-target { margin: 0 0 7px 2px; color: var(--muted-foreground); font-size: .7rem; }
.pagination { display: flex; align-items: center; justify-content: center; gap: 20px; margin-top: 25px; font-size: .8rem; }
footer { display: flex; gap: 12px; justify-content: space-between; flex-wrap: wrap; color: var(--muted-foreground); font-size: .65rem; margin-top: 35px; }
@media (max-width: 680px) { .experience { padding: 24px 12px 90px; }.experience-header { align-items: start; flex-direction: column; gap: 20px; padding-right: 32px; }h1 { font-size: 1.65rem; }.experience-panel { padding: 15px; }.card-grid { grid-template-columns: 1fr; }.selection { padding: 14px; }.hint { font-size: .7rem; } }
</style>
