<script setup lang="ts">
import { ref, computed } from 'vue'
import { getIconUrl } from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'

const props = defineProps<{
  weeklyAppSummaries: { appId: number; appName: string; totalSeconds: number }[]
  weeklyTotalSeconds: number
}>()

const DONUT_MAX_ITEMS = 10
const CIRCUMFERENCE = 2 * Math.PI * 70
const CHART_COLORS = [
  '#58a6ff', '#2ea043', '#d29922', '#f85149', '#bc8cff',
  '#79c0ff', '#56d364', '#e3b341', '#ff7b72', '#d2a8ff',
]

const hoveredSegment = ref<number | null>(null)

const donutSegments = computed(() => {
  const total = props.weeklyTotalSeconds
  if (total === 0) return []

  const items = props.weeklyAppSummaries.slice(0, DONUT_MAX_ITEMS)
  const otherSeconds = props.weeklyAppSummaries
    .slice(DONUT_MAX_ITEMS)
    .reduce((s, a) => s + a.totalSeconds, 0)

  const all = otherSeconds > 0
    ? [...items, { appId: 0, appName: '其他', totalSeconds: otherSeconds }]
    : items

  let offset = 0
  return all.map((app, i) => {
    const fraction = app.totalSeconds / total
    const length = fraction * CIRCUMFERENCE
    const seg = {
      appId: app.appId,
      appName: app.appName,
      totalSeconds: app.totalSeconds,
      percentage: (fraction * 100).toFixed(1),
      color: CHART_COLORS[i % CHART_COLORS.length],
      length,
      offset,
    }
    offset += length
    return seg
  })
})
</script>

<template>
  <section class="panel">
    <h2>本周应用使用</h2>
    <div v-if="donutSegments.length" class="weekly-chart">
      <div class="donut-wrapper">
        <svg viewBox="0 0 200 200" class="donut-svg">
          <circle cx="100" cy="100" r="70" fill="none" stroke="#1f1f1f" stroke-width="30" />
          <circle
            v-for="(seg, i) in donutSegments"
            :key="i"
            cx="100" cy="100" r="70"
            fill="none"
            :stroke="seg.color"
            :stroke-width="hoveredSegment === i ? 35 : 30"
            :stroke-dasharray="`${seg.length} ${CIRCUMFERENCE - seg.length}`"
            :stroke-dashoffset="`${-seg.offset}`"
            transform="rotate(-90 100 100)"
            class="donut-segment"
            @mouseenter="hoveredSegment = i"
            @mouseleave="hoveredSegment = null"
          />
        </svg>
        <div class="donut-center">
          <template v-if="hoveredSegment !== null">
            <img
              :src="getIconUrl(donutSegments[hoveredSegment].appId)"
              class="donut-center-icon"
              @error="($event.target as HTMLImageElement).style.display = 'none'"
            />
            <span class="donut-app">{{ donutSegments[hoveredSegment].appName }}</span>
            <span class="donut-dur">{{ formatDuration(donutSegments[hoveredSegment].totalSeconds) }}</span>
            <span class="donut-pct">{{ donutSegments[hoveredSegment].percentage }}%</span>
          </template>
          <template v-else>
            <span class="donut-app">本周总计</span>
            <span class="donut-dur">{{ formatDuration(weeklyTotalSeconds) }}</span>
          </template>
        </div>
      </div>
      <div class="donut-legend">
        <div
          v-for="(seg, i) in donutSegments"
          :key="seg.appName"
          class="legend-item"
          :class="{ dimmed: hoveredSegment !== null && hoveredSegment !== i }"
          @mouseenter="hoveredSegment = i"
          @mouseleave="hoveredSegment = null"
        >
          <span class="legend-dot" :style="{ background: seg.color }"></span>
          <img
            :src="getIconUrl(seg.appId)"
            class="legend-icon"
            @error="($event.target as HTMLImageElement).style.display = 'none'"
          />
          <span class="legend-name">{{ seg.appName }}</span>
          <span class="legend-dur">{{ formatDuration(seg.totalSeconds) }}</span>
          <span class="legend-pct">{{ seg.percentage }}%</span>
        </div>
      </div>
    </div>
    <div v-else class="empty">暂无数据</div>
  </section>
</template>

<style scoped>
.weekly-chart {
  display: flex;
  gap: 2rem;
  align-items: center;
}

.donut-wrapper {
  position: relative;
  width: 200px;
  height: 200px;
  flex-shrink: 0;
}

.donut-svg {
  width: 100%;
  height: 100%;
  transform: rotate(0deg);
  overflow: visible;
}

.donut-segment {
  transition: stroke-width 0.2s ease, opacity 0.2s;
  cursor: pointer;
}

.donut-center {
  position: absolute;
  top: 0; left: 0; right: 0; bottom: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  pointer-events: none;
}

.donut-center-icon {
  width: 24px;
  height: 24px;
  border-radius: 4px;
  object-fit: contain;
  margin-bottom: 4px;
}

.donut-app {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text);
  max-width: 100px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  text-align: center;
}

.donut-dur {
  font-size: 1rem;
  font-family: var(--font-mono);
  color: var(--text);
  font-weight: 700;
}

.donut-pct {
  font-size: 0.75rem;
  color: var(--text-dim);
}

.donut-legend {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  max-height: 200px;
  overflow-y: auto;
  padding-right: 4px;
}

.legend-item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.8rem;
  padding: 0.25rem 0.5rem;
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.2s, opacity 0.2s;
}

.legend-item:hover {
  background: rgba(255, 255, 255, 0.05);
}

.legend-item.dimmed {
  opacity: 0.3;
}

.legend-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.legend-icon {
  width: 16px;
  height: 16px;
  border-radius: 3px;
  object-fit: contain;
  flex-shrink: 0;
}

.legend-name {
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.legend-dur {
  font-family: var(--font-mono);
  color: var(--text-dim);
}

.legend-pct {
  width: 3rem;
  text-align: right;
  color: var(--text-dim);
}

.empty { text-align: center; padding: 2rem; color: var(--text-dim); font-size: 0.9rem; }
@media (max-width: 640px) {
  .weekly-chart {
    flex-direction: column;
  }
}
</style>