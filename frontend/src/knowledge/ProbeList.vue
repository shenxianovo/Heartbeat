<script setup lang="ts">
import type {
  IEpisodeResponse, IStrandResponse, IMatcherDto,
  IResolveProbeRequest, IPromoteEpisodeRequest,
} from '../api/index'

const props = defineProps<{
  episodes: IEpisodeResponse[]
  strands: IStrandResponse[]
  conflictError: string | null
}>()

const emit = defineEmits<{
  (e: 'resolve', probeId: string, req: IResolveProbeRequest): void
  (e: 'promote', episodeId: string, req: IPromoteEpisodeRequest): void
  (e: 'mute', matcher: IMatcherDto): void
  (e: 'reload'): void
}>()

interface ProbeRow {
  probeId: string
  episodeId: string
  episodeText: string
  matcher: IMatcherDto | undefined
  status: string
  createdAt: Date | undefined
}

function activeProbes(): ProbeRow[] {
  const rows: ProbeRow[] = []
  for (const ep of props.episodes) {
    for (const p of ep.probes ?? []) {
      if (p.status === 'active') {
        rows.push({
          probeId: p.id!,
          episodeId: ep.id!,
          episodeText: ep.text ?? '',
          matcher: p.matcher,
          status: p.status!,
          createdAt: p.createdAt,
        })
      }
    }
  }
  return rows
}

function resolvedProbes(): ProbeRow[] {
  const rows: ProbeRow[] = []
  for (const ep of props.episodes) {
    for (const p of ep.probes ?? []) {
      if (p.status !== 'active') {
        rows.push({
          probeId: p.id!,
          episodeId: ep.id!,
          episodeText: ep.text ?? '',
          matcher: p.matcher,
          status: p.status!,
          createdAt: p.createdAt,
        })
      }
    }
  }
  return rows
}

function doResolve(row: ProbeRow, resolution: string) {
  emit('resolve', row.probeId, { resolution, expectedVersion: 0 })
}

function doMute(row: ProbeRow) {
  if (row.matcher) emit('mute', row.matcher)
}

function matcherDisplay(m: IMatcherDto | undefined): string {
  if (!m) return '—'
  return (m.steps ?? []).map(s => `${s.reading}${s.op}${s.value}`).join(' → ')
}

function formatDate(d: Date | undefined): string {
  if (!d || !(d instanceof Date) || isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}
</script>

<template>
  <div class="probe-list">
    <div v-if="conflictError" class="conflict-banner">
      <span>{{ conflictError }}</span>
      <button class="btn-sm" @click="emit('reload')">刷新</button>
    </div>

    <section class="probe-section">
      <h3>活跃探针 ({{ activeProbes().length }})</h3>
      <p v-if="activeProbes().length === 0" class="placeholder">无活跃探针</p>
      <div v-for="row in activeProbes()" :key="row.probeId" class="probe-card">
        <div class="probe-header">
          <span class="probe-matcher">{{ matcherDisplay(row.matcher) }}</span>
          <span class="probe-date">{{ formatDate(row.createdAt) }}</span>
        </div>
        <p class="probe-episode">{{ row.episodeText }}</p>
        <div class="probe-actions">
          <button class="btn-sm primary" @click="doResolve(row, 'promoted')">提升</button>
          <button class="btn-sm" @click="doResolve(row, 'denied')">否认</button>
          <button class="btn-sm" @click="doMute(row)">静音</button>
        </div>
      </div>
    </section>

    <section class="probe-section">
      <h3>已解决 ({{ resolvedProbes().length }})</h3>
      <p v-if="resolvedProbes().length === 0" class="placeholder">无已解决探针</p>
      <div v-for="row in resolvedProbes()" :key="row.probeId" class="probe-card resolved">
        <div class="probe-header">
          <span class="probe-matcher">{{ matcherDisplay(row.matcher) }}</span>
          <span class="probe-status">{{ row.status }}</span>
        </div>
        <p class="probe-episode">{{ row.episodeText }}</p>
      </div>
    </section>
  </div>
</template>

<style scoped>
.probe-list { max-width: 800px; }
.conflict-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
  padding: 0.5rem 0.75rem;
  border: 1px solid rgb(239 68 68 / 0.3);
  background: rgb(239 68 68 / 0.1);
  border-radius: 6px;
  color: rgb(252 165 165);
  font-size: 0.8rem;
}
.probe-section { margin-bottom: 1.5rem; }
.probe-section h3 { font-size: 0.9rem; font-weight: 600; margin-bottom: 0.75rem; }
.placeholder { color: var(--muted-foreground); font-size: 0.85rem; }
.probe-card {
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 0.75rem;
  margin-bottom: 0.5rem;
}
.probe-card.resolved { opacity: 0.6; }
.probe-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 0.3rem;
}
.probe-matcher { font-size: 0.75rem; color: var(--muted-foreground); font-family: monospace; }
.probe-date { font-size: 0.7rem; color: var(--muted-foreground); }
.probe-status {
  font-size: 0.65rem;
  background: var(--border);
  padding: 0.1rem 0.3rem;
  border-radius: 4px;
}
.probe-episode { font-size: 0.85rem; line-height: 1.5; margin-bottom: 0.5rem; }
.probe-actions { display: flex; gap: 0.4rem; }
.btn-sm {
  font-size: 0.75rem;
  padding: 0.25rem 0.5rem;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: var(--card);
  color: var(--foreground);
  cursor: pointer;
}
.btn-sm.primary {
  background: var(--primary);
  color: var(--primary-foreground, #fff);
  border-color: var(--primary);
}
</style>
