<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { getIconUrl } from '../api/index'
import type { AppUsageResponse } from '../api/index'
import { useTimelineDrag } from '../composables/useTimelineDrag'
import { Card } from '@/components/ui/card'
import { LayoutGrid, AlignJustify } from 'lucide-vue-next'
import { parseUsage, buildRows, mergeActivityBursts, initialViewBounds } from '../timeline/timelineModel'
import { niceTicks } from '../timeline/timeScale'

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

// --- Drag interaction (composable) ---
const {
  isDraggingTimeline,
  handleWheel,
  timelinePointerDown,
  minimapPointerDown,
} = useTimelineDrag(viewStart, viewEnd, timelineEl, computed(() => props.selectedDate))

// Initialize view bounds
const initViewBounds = () => {
  const b = initialViewBounds(props.selectedDate, props.isToday, props.usageData, Date.now())
  viewStart.value = b.start
  viewEnd.value = b.end
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

// ========== 模型（纯逻辑在 timeline/timelineModel.ts，此处只做响应式接线与显示映射） ==========

const parsed = computed(() => parseUsage(props.usageData))

const detailedRows = computed(() =>
  buildRows(parsed.value, { start: viewStart.value, end: viewEnd.value }).map(row => ({
    ...row,
    name: row.isAway ? '离开' : (props.appNameMap.get(row.appId) || `App ${row.appId}`),
  }))
)

// Adaptive tick intervals based on available width
const ticks = computed(() => {
  const trackWidth = (containerWidth.value || 800) - 120
  const maxTicks = Math.max(2, Math.floor(trackWidth / 70))
  return niceTicks(viewStart.value, viewEnd.value, maxTicks)
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
  return mergeActivityBursts(parsed.value).map(iv => {
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
                  v-for="(bar, idx) in row.bars"
                  :key="idx"
                  class="absolute top-2.5 h-5 cursor-pointer rounded-sm opacity-80 hover:z-[3] hover:opacity-100"
                  :class="row.isAway ? 'bg-muted-foreground/40' : 'bg-primary'"
                  :style="{ left: bar.left + '%', width: bar.width + '%' }"
                  :title="bar.label"
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
