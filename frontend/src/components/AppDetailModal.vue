<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { getIconUrl, fetchPublicSegments } from '../api/index'
import type { AppUsageResponse, SegmentResponse } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'
import { X } from 'lucide-vue-next'

const props = defineProps<{
  username: string
  deviceId: number
  selectedDate: string
  app: { appId: number; appName: string; totalSeconds: number }
  usageData: AppUsageResponse[]
  titleBreakdown: (appId: number) => { title: string; secondary?: string; category?: string; totalSeconds: number; count: number }[]
}>()

const emit = defineEmits<{ close: [] }>()

// ── 插件段(非 system 轨) ──
const pluginSegments = ref<SegmentResponse[]>([])
const loading = ref(false)

async function loadSegments() {
  loading.value = true
  try {
    const dateObj = new Date(props.selectedDate + 'T00:00:00')
    pluginSegments.value = await fetchPublicSegments(props.username, {
      deviceId: props.deviceId,
      appId: props.app.appId,
      start: dateObj.toISOString(),
      end: new Date(dateObj.getTime() + 86400000).toISOString(),
    })
  } finally {
    loading.value = false
  }
}

watch(() => props.app.appId, loadSegments, { immediate: true })

// ── 多轨回放(第一版:静态视窗,自动取当天活动区间) ──

interface TrackBar { left: number; width: number; tooltip: string; isPoint: boolean }
interface Track { source: string; bars: TrackBar[] }

const systemSegments = computed(() =>
  props.usageData.filter(u => u.appId === props.app.appId && u.startTime && u.endTime)
)

/** 全部轨道数据的时间包络,前后各 pad 3%,作为静态视窗。 */
const viewBounds = computed(() => {
  let min = Infinity, max = -Infinity
  for (const u of systemSegments.value) {
    min = Math.min(min, u.startTime!.getTime())
    max = Math.max(max, u.endTime!.getTime())
  }
  for (const s of pluginSegments.value) {
    if (!s.startTime || !s.endTime) continue
    min = Math.min(min, s.startTime.getTime())
    max = Math.max(max, s.endTime.getTime())
  }
  if (!isFinite(min) || max <= min) return null
  const pad = Math.max((max - min) * 0.03, 60_000)
  return { start: min - pad, end: max + pad }
})

const fmtTime = (t: number) => {
  const d = new Date(t)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

function toBar(start: Date, end: Date, label: string): TrackBar | null {
  const vb = viewBounds.value
  if (!vb) return null
  const range = vb.end - vb.start
  const s = start.getTime(), e = end.getTime()
  const left = ((s - vb.start) / range) * 100
  const isPoint = e - s < 1000 // 点事件:零长段(ADR-017)
  const width = isPoint ? 0 : Math.max(0.4, ((e - s) / range) * 100)
  const time = isPoint ? fmtTime(s) : `${fmtTime(s)} - ${fmtTime(e)}`
  return { left, width, tooltip: label ? `${time}  ${label}` : time, isPoint }
}

const tracks = computed<Track[]>(() => {
  const result: Track[] = []

  const sysBars = systemSegments.value
    .map(u => toBar(u.startTime!, u.endTime!, u.title ?? ''))
    .filter((b): b is TrackBar => b !== null)
  if (sysBars.length) result.push({ source: 'system', bars: sysBars })

  // 每个插件 source 一条轨(回放多轨叠加,ADR-017 §4)
  const bySource = new Map<string, TrackBar[]>()
  for (const s of pluginSegments.value) {
    if (!s.startTime || !s.endTime || !s.source) continue
    // attributes 是各 source 自由结构的原始 JSON,v1 直接进 tooltip
    const label = [s.title ?? s.identityKey, s.attributes].filter(Boolean).join('  ')
    const bar = toBar(s.startTime, s.endTime, label)
    if (!bar) continue
    let arr = bySource.get(s.source)
    if (!arr) { arr = []; bySource.set(s.source, arr) }
    arr.push(bar)
  }
  for (const [source, bars] of bySource) result.push({ source, bars })

  return result
})

const timeTicks = computed(() => {
  const vb = viewBounds.value
  if (!vb) return []
  const range = vb.end - vb.start
  const niceIntervals = [300_000, 900_000, 1_800_000, 3_600_000, 7_200_000, 10_800_000, 21_600_000]
  let interval = niceIntervals[niceIntervals.length - 1]
  for (const ni of niceIntervals) {
    if (range / ni <= 10) { interval = ni; break }
  }
  const ticks: { percent: number; label: string }[] = []
  for (let t = Math.ceil(vb.start / interval) * interval; t <= vb.end; t += interval) {
    ticks.push({ percent: ((t - vb.start) / range) * 100, label: fmtTime(t) })
  }
  return ticks
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
            :src="getIconUrl(app.appId)"
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
              <!-- 轨道行:system 主轨在前,插件轨挂在下方 -->
              <div
                v-for="track in tracks"
                :key="track.source"
                class="flex h-9 border-b border-border last:border-b-0"
              >
                <div class="flex w-[80px] shrink-0 items-center border-r border-border bg-muted px-2">
                  <span class="truncate font-mono text-[0.7rem] text-muted-foreground">{{ track.source }}</span>
                </div>
                <div class="relative flex-1">
                  <template v-for="(bar, i) in track.bars" :key="i">
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
            <div v-else class="rounded-md border border-border bg-secondary py-6 text-center text-[0.8rem] text-muted-foreground">
              {{ loading ? '加载中…' : '当日无回放数据' }}
            </div>
          </section>

          <!-- 标题明细 -->
          <section>
            <h3 class="mb-2 text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">标题明细</h3>
            <div class="flex flex-col gap-2">
              <div
                v-for="(t, ti) in titleBreakdown(app.appId)"
                :key="ti"
                class="flex items-center gap-2 text-[0.8rem]"
                :class="t.category === 'system' ? 'opacity-50' : ''"
              >
                <span class="flex min-w-0 flex-1 flex-col">
                  <span class="truncate" :class="t.title ? '' : 'text-muted-foreground italic'" :title="t.title">{{ t.title || '无标题窗口' }}</span>
                  <span v-if="t.secondary" class="truncate text-[0.65rem] text-muted-foreground" :title="t.secondary">{{ t.secondary }}</span>
                </span>
                <span class="shrink-0 text-[0.7rem] text-muted-foreground">×{{ t.count }}</span>
                <span class="shrink-0 font-mono text-[0.75rem] text-muted-foreground">{{ formatDuration(t.totalSeconds) }}</span>
              </div>
              <div
                v-if="titleBreakdown(app.appId).length === 0"
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
