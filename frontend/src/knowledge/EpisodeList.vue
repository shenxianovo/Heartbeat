<script setup lang="ts">
import { ref, watch } from 'vue'
import type {
  IEpisodeResponse, IStrandResponse,
  ICreateEpisodeRequest, IUpdateEpisodeRequest,
  IRelateEpisodeRequest, IPromoteEpisodeRequest,
} from '../api/index'
import { Button } from '@/components/ui/button'

const props = defineProps<{
  episodes: IEpisodeResponse[]
  loading: boolean
  error: string | null
  conflictError: string | null
  strands: IStrandResponse[]
  filterDate: string | null
  filterStrandId: string | null
  filterUnrelated: boolean
}>()

const emit = defineEmits<{
  (e: 'update:filterDate', val: string | null): void
  (e: 'update:filterStrandId', val: string | null): void
  (e: 'update:filterUnrelated', val: boolean): void
  (e: 'reload'): void
  (e: 'create', req: ICreateEpisodeRequest): void
  (e: 'update', id: string, req: IUpdateEpisodeRequest): void
  (e: 'relate', id: string, req: IRelateEpisodeRequest): void
  (e: 'delete', id: string, version: number): void
  (e: 'promote', id: string, req: IPromoteEpisodeRequest): void
}>()

type EditMode = 'none' | 'create' | 'edit' | 'relate' | 'promote'
const editMode = ref<EditMode>('none')
const editingId = ref<string | null>(null)

const formText = ref('')
const formLocalDate = ref('')
const formRelatedStrandId = ref<string | null>(null)

const promoteNewStrandName = ref('')
const promoteParentId = ref<string | null>(null)

function startCreate() {
  editMode.value = 'create'
  editingId.value = null
  formText.value = ''
  formLocalDate.value = todayStr()
  formRelatedStrandId.value = null
}

function startEdit(ep: IEpisodeResponse) {
  editMode.value = 'edit'
  editingId.value = ep.id!
  formText.value = ep.text ?? ''
  formLocalDate.value = ep.localDate ? formatDate(ep.localDate) : todayStr()
  formRelatedStrandId.value = ep.relatedStrandId ?? null
}

function startRelate(ep: IEpisodeResponse) {
  editMode.value = 'relate'
  editingId.value = ep.id!
  formRelatedStrandId.value = ep.relatedStrandId ?? null
}

function startPromote(ep: IEpisodeResponse) {
  editMode.value = 'promote'
  editingId.value = ep.id!
  promoteNewStrandName.value = ''
  promoteParentId.value = null
}

function cancel() {
  editMode.value = 'none'
  editingId.value = null
}

function submitCreate() {
  if (!formText.value.trim()) return
  emit('create', {
    text: formText.value.trim(),
    localDate: new Date(formLocalDate.value),
    relatedStrandId: formRelatedStrandId.value ?? undefined,
  })
  cancel()
}

function submitEdit() {
  if (!formText.value.trim() || !editingId.value) return
  const ep = props.episodes.find(e => e.id === editingId.value)
  emit('update', editingId.value, {
    text: formText.value.trim(),
    localDate: new Date(formLocalDate.value),
    expectedVersion: ep?.version,
  })
  cancel()
}

function submitRelate() {
  if (!editingId.value) return
  const ep = props.episodes.find(e => e.id === editingId.value)
  emit('relate', editingId.value, {
    relatedStrandId: formRelatedStrandId.value ?? undefined,
    expectedVersion: ep?.version,
  })
  cancel()
}

function submitPromote() {
  if (!editingId.value || !promoteNewStrandName.value.trim()) return
  const ep = props.episodes.find(e => e.id === editingId.value)
  emit('promote', editingId.value, {
    strandName: promoteNewStrandName.value.trim(),
    parentStrandId: promoteParentId.value ?? undefined,
    expectedVersion: ep?.version,
  })
  cancel()
}

function doDelete(ep: IEpisodeResponse) {
  emit('delete', ep.id!, ep.version!)
}

function applyFilter() {
  emit('reload')
}

watch([() => props.filterDate, () => props.filterStrandId, () => props.filterUnrelated], () => {
  applyFilter()
})

function todayStr(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

function formatDate(d: Date): string {
  if (!(d instanceof Date) || isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

function strandName(id: string | undefined | null): string {
  if (!id) return ''
  return props.strands.find(s => s.id === id)?.name ?? id.slice(0, 8)
}
</script>

<template>
  <div class="episode-list">
    <div v-if="conflictError" class="conflict-banner">
      <span>{{ conflictError }}</span>
      <Button variant="glass" size="xs" @click="emit('reload')">刷新</Button>
    </div>

    <!-- Filters -->
    <div class="filters">
      <input
        type="date"
        :value="filterDate ?? ''"
        class="input"
        @input="emit('update:filterDate', ($event.target as HTMLInputElement).value || null)"
      />
      <select
        :value="filterStrandId ?? ''"
        class="input"
        @change="emit('update:filterStrandId', ($event.target as HTMLSelectElement).value || null)"
      >
        <option value="">所有脉络</option>
        <option v-for="s in strands" :key="s.id" :value="s.id">
          {{ s.path?.join(' → ') || s.name }}
        </option>
      </select>
      <label class="toggle-label">
        <input
          type="checkbox"
          :checked="filterUnrelated"
          @change="emit('update:filterUnrelated', ($event.target as HTMLInputElement).checked)"
        />
        仅未关联
      </label>
      <Button variant="glass" size="xs" @click="startCreate">新建</Button>
    </div>

    <!-- Create/Edit form -->
    <div v-if="editMode === 'create' || editMode === 'edit'" class="form-section">
      <label class="field">
        <span>内容</span>
        <textarea v-model="formText" class="input textarea" rows="3" />
      </label>
      <label class="field">
        <span>日期</span>
        <input v-model="formLocalDate" type="date" class="input" />
      </label>
      <label class="field">
        <span>关联脉络（可选）</span>
        <select v-model="formRelatedStrandId" class="input">
          <option :value="null">无</option>
          <option v-for="s in strands" :key="s.id" :value="s.id">
            {{ s.path?.join(' → ') || s.name }}
          </option>
        </select>
      </label>
      <div class="form-actions">
        <Button
          variant="glassPrimary"
          size="xs"
          :disabled="!formText.trim()"
          @click="editMode === 'create' ? submitCreate() : submitEdit()"
        >
          {{ editMode === 'create' ? '创建' : '保存' }}
        </Button>
        <Button variant="glass" size="xs" @click="cancel">取消</Button>
      </div>
    </div>

    <!-- Relate form -->
    <div v-if="editMode === 'relate'" class="form-section">
      <label class="field">
        <span>关联到脉络</span>
        <select v-model="formRelatedStrandId" class="input">
          <option :value="null">解除关联</option>
          <option v-for="s in strands" :key="s.id" :value="s.id">
            {{ s.path?.join(' → ') || s.name }}
          </option>
        </select>
      </label>
      <div class="form-actions">
        <Button variant="glassPrimary" size="xs" @click="submitRelate">确认</Button>
        <Button variant="glass" size="xs" @click="cancel">取消</Button>
      </div>
    </div>

    <!-- Promote form -->
    <div v-if="editMode === 'promote'" class="form-section">
      <p class="hint">将此片段事实提升为持续脉络（原记录保留）。</p>
      <label class="field">
        <span>新脉络名称</span>
        <input v-model="promoteNewStrandName" class="input" />
      </label>
      <label class="field">
        <span>父级（可选）</span>
        <select v-model="promoteParentId" class="input">
          <option :value="null">顶层</option>
          <option v-for="s in strands" :key="s.id" :value="s.id">
            {{ s.path?.join(' → ') || s.name }}
          </option>
        </select>
      </label>
      <div class="form-actions">
        <Button variant="glassPrimary" size="xs" :disabled="!promoteNewStrandName.trim()" @click="submitPromote">
          提升
        </Button>
        <Button variant="glass" size="xs" @click="cancel">取消</Button>
      </div>
    </div>

    <p v-if="loading" class="placeholder">加载中…</p>
    <p v-else-if="error" class="error-text">{{ error }}</p>
    <p v-else-if="episodes.length === 0" class="placeholder">暂无片段事实</p>

    <div v-else class="list">
      <div v-for="ep in episodes" :key="ep.id" class="episode-card">
        <div class="ep-header">
          <span class="ep-date">{{ ep.localDate ? formatDate(ep.localDate) : '' }}</span>
          <span v-if="ep.relatedStrandId" class="strand-badge">{{ strandName(ep.relatedStrandId) }}</span>
          <span v-else class="unrelated-badge">未关联</span>
        </div>
        <p class="ep-text">{{ ep.text }}</p>
        <div class="ep-actions">
          <Button variant="glass" size="xs" @click="startEdit(ep)">编辑</Button>
          <Button variant="glass" size="xs" @click="startRelate(ep)">关联</Button>
          <Button variant="glass" size="xs" @click="startPromote(ep)">提升</Button>
          <Button variant="glassDestructive" size="xs" @click="doDelete(ep)">删除</Button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.episode-list { max-width: 800px; }
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
.filters {
  display: flex;
  gap: 0.5rem;
  align-items: center;
  flex-wrap: wrap;
  margin-bottom: 1rem;
}
.toggle-label {
  font-size: 0.8rem;
  display: flex;
  align-items: center;
  gap: 0.3rem;
  color: var(--muted-foreground);
}
.form-section {
  margin-bottom: 1rem;
  padding: 0.75rem;
  background: var(--card);
  border: 1px solid var(--border);
  border-radius: 6px;
}
.field {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  margin-bottom: 0.5rem;
  font-size: 0.8rem;
}
.field span { color: var(--muted-foreground); }
.input {
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: transparent;
  color: var(--foreground);
  font-size: 0.8rem;
}
.textarea { resize: vertical; font-family: inherit; }
.form-actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
.hint { font-size: 0.8rem; color: var(--muted-foreground); margin-bottom: 0.5rem; }
.placeholder { color: var(--muted-foreground); font-size: 0.85rem; }
.error-text { color: rgb(239 68 68); font-size: 0.85rem; }
.list { display: flex; flex-direction: column; gap: 0.75rem; }
.episode-card {
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 0.75rem;
}
.ep-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.4rem;
}
.ep-date { font-size: 0.75rem; color: var(--muted-foreground); }
.strand-badge {
  font-size: 0.65rem;
  background: var(--primary);
  color: var(--primary-foreground, #fff);
  padding: 0.1rem 0.4rem;
  border-radius: 999px;
}
.unrelated-badge {
  font-size: 0.65rem;
  background: var(--border);
  color: var(--muted-foreground);
  padding: 0.1rem 0.4rem;
  border-radius: 999px;
}
.ep-text { font-size: 0.85rem; margin-bottom: 0.5rem; line-height: 1.5; }
.ep-actions { display: flex; gap: 0.4rem; }
</style>
