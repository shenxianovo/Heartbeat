<script setup lang="ts">
import { ref, watch } from 'vue'
import type { IStrandResponse, IUpdateStrandRequest, IMoveStrandRequest, IEndStrandRequest, IMatcherDto } from '../api/index'

const props = defineProps<{
  strand: IStrandResponse
  strands: IStrandResponse[]
  conflictError: string | null
}>()

const emit = defineEmits<{
  (e: 'update', id: string, req: IUpdateStrandRequest): void
  (e: 'move', id: string, req: IMoveStrandRequest): void
  (e: 'end', id: string, req: IEndStrandRequest): void
  (e: 'mute', matcher: IMatcherDto): void
  (e: 'refresh'): void
  (e: 'deselect'): void
}>()

type Mode = 'view' | 'edit' | 'move'
const mode = ref<Mode>('view')

const editName = ref('')
const editGloss = ref('')
const editStartedOn = ref('')
const editEndedOn = ref('')

const moveIntent = ref<'correct' | 'successor' | null>(null)
const moveNewParentId = ref<string | null>(null)

watch(() => props.strand.id, () => {
  mode.value = 'view'
  moveIntent.value = null
})

function startEdit() {
  editName.value = props.strand.name ?? ''
  editGloss.value = props.strand.gloss ?? ''
  editStartedOn.value = props.strand.startedOn ? formatDateInput(props.strand.startedOn) : ''
  editEndedOn.value = props.strand.endedOn ? formatDateInput(props.strand.endedOn) : ''
  mode.value = 'edit'
}

function submitEdit() {
  emit('update', props.strand.id!, {
    name: editName.value.trim(),
    gloss: editGloss.value.trim(),
    startedOn: editStartedOn.value ? new Date(editStartedOn.value) : undefined,
    endedOn: editEndedOn.value ? new Date(editEndedOn.value) : undefined,
    expectedVersion: props.strand.version,
  })
  mode.value = 'view'
}

function startMove() {
  moveIntent.value = null
  moveNewParentId.value = null
  mode.value = 'move'
}

function submitMove() {
  if (moveIntent.value === 'correct') {
    emit('move', props.strand.id!, {
      newParentStrandId: moveNewParentId.value ?? undefined,
      expectedVersion: props.strand.version,
    })
  }
  mode.value = 'view'
}

function submitEnd() {
  emit('end', props.strand.id!, {
    endedOn: new Date(),
    expectedVersion: props.strand.version,
  })
}

function parentOptions() {
  return props.strands.filter(s =>
    s.id !== props.strand.id && !isDescendant(s.id!, props.strand.id!),
  )
}

function isDescendant(candidateId: string, rootId: string): boolean {
  let current = props.strands.find(s => s.id === candidateId)
  while (current?.parentStrandId) {
    if (current.parentStrandId === rootId) return true
    current = props.strands.find(s => s.id === current!.parentStrandId)
  }
  return false
}

function formatDateInput(d: Date): string {
  if (!(d instanceof Date) || isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

function matcherDisplay(m: IMatcherDto): string {
  return (m.steps ?? []).map(s => `${s.reading}${s.op}${s.value}`).join(' → ')
}
</script>

<template>
  <div class="strand-detail">
    <div v-if="conflictError" class="conflict-banner">
      <span>{{ conflictError }}</span>
      <button class="btn-sm" @click="emit('refresh')">刷新</button>
    </div>

    <div class="detail-header">
      <h2>{{ strand.name }}</h2>
      <button class="btn-sm" @click="emit('deselect')">关闭</button>
    </div>

    <div class="detail-meta">
      <div v-if="strand.path?.length" class="path">{{ strand.path.join(' → ') }}</div>
      <div v-if="strand.gloss" class="gloss">{{ strand.gloss }}</div>
      <div class="meta-row">
        <span v-if="strand.startedOn || strand.endedOn" class="date-range">
          {{ strand.startedOn ? formatDateInput(strand.startedOn) : '…' }}
          ~
          {{ strand.endedOn ? formatDateInput(strand.endedOn) : '至今' }}
        </span>
        <span class="version">v{{ strand.version }}</span>
      </div>
    </div>

    <!-- View mode actions -->
    <div v-if="mode === 'view'" class="actions">
      <button class="btn-sm" @click="startEdit">编辑</button>
      <button class="btn-sm" @click="startMove">变更父级</button>
      <button v-if="!strand.endedOn" class="btn-sm danger" @click="submitEnd">结束</button>
    </div>

    <!-- Edit mode -->
    <div v-if="mode === 'edit'" class="form-section">
      <label class="field">
        <span>名称</span>
        <input v-model="editName" class="input" />
      </label>
      <label class="field">
        <span>释义</span>
        <input v-model="editGloss" class="input" />
      </label>
      <label class="field">
        <span>开始日期</span>
        <input v-model="editStartedOn" type="date" class="input" />
      </label>
      <label class="field">
        <span>结束日期</span>
        <input v-model="editEndedOn" type="date" class="input" />
      </label>
      <div class="form-actions">
        <button class="btn-sm primary" :disabled="!editName.trim()" @click="submitEdit">保存</button>
        <button class="btn-sm" @click="mode = 'view'">取消</button>
      </div>
    </div>

    <!-- Move mode -->
    <div v-if="mode === 'move'" class="form-section">
      <p v-if="!moveIntent" class="intent-prompt">这次变更的性质是？</p>
      <div v-if="!moveIntent" class="intent-choices">
        <button class="intent-btn correct" @click="moveIntent = 'correct'">
          <strong>纠正历史归属</strong>
          <span>过去归错了，现在修正</span>
        </button>
        <button class="intent-btn successor" @click="moveIntent = 'successor'">
          <strong>现实归属变化</strong>
          <span>结束当前，在新父级下建后继</span>
        </button>
      </div>

      <template v-if="moveIntent === 'correct'">
        <div class="warning">
          这会重写历史层级解释，相关 Recap 可能变为过期状态。
        </div>
        <label class="field">
          <span>新父级</span>
          <select v-model="moveNewParentId" class="input">
            <option :value="null">顶层（无父级）</option>
            <option v-for="s in parentOptions()" :key="s.id" :value="s.id">
              {{ s.path?.join(' → ') || s.name }}
            </option>
          </select>
        </label>
        <div class="form-actions">
          <button class="btn-sm primary" @click="submitMove">确认移动</button>
          <button class="btn-sm" @click="mode = 'view'">取消</button>
        </div>
      </template>

      <template v-if="moveIntent === 'successor'">
        <p class="hint">
          请先结束当前脉络，然后在新父级下创建后继节点（名称和释义会预填）。
        </p>
        <div class="form-actions">
          <button class="btn-sm danger" @click="submitEnd">结束当前脉络</button>
          <button class="btn-sm" @click="mode = 'view'">取消</button>
        </div>
      </template>
    </div>

    <!-- Matchers -->
    <div class="matchers-section">
      <h3>指纹 ({{ strand.members?.length ?? 0 }})</h3>
      <div v-if="!strand.members?.length" class="placeholder">无指纹（纯语境容器）</div>
      <div v-for="m in strand.members" :key="matcherDisplay(m)" class="matcher-item">
        <span class="matcher-text">{{ matcherDisplay(m) }}</span>
        <button class="btn-sm danger" @click="emit('mute', m)">静音</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.strand-detail {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 1rem;
}
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
.detail-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
}
.detail-header h2 { font-size: 1.1rem; font-weight: 600; }
.detail-meta { margin-bottom: 1rem; }
.path {
  font-size: 0.75rem;
  color: var(--muted-foreground);
  margin-bottom: 0.25rem;
}
.gloss {
  font-size: 0.85rem;
  color: var(--muted-foreground);
  margin-bottom: 0.25rem;
}
.meta-row { display: flex; gap: 1rem; font-size: 0.75rem; color: var(--muted-foreground); }
.actions { display: flex; gap: 0.5rem; margin-bottom: 1rem; }
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
.btn-sm.danger {
  border-color: rgb(239 68 68 / 0.5);
  color: rgb(239 68 68);
}
.btn-sm:disabled { opacity: 0.5; cursor: default; }
.form-section {
  margin-bottom: 1rem;
  padding: 0.75rem;
  background: var(--card);
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
.form-actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
.intent-prompt { font-size: 0.85rem; margin-bottom: 0.5rem; }
.intent-choices { display: flex; flex-direction: column; gap: 0.5rem; }
.intent-btn {
  text-align: left;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  color: var(--foreground);
}
.intent-btn:hover { background: var(--card); }
.intent-btn strong { display: block; font-size: 0.85rem; }
.intent-btn span { font-size: 0.75rem; color: var(--muted-foreground); }
.intent-btn.correct:hover { border-color: rgb(239 68 68 / 0.5); }
.warning {
  margin-bottom: 0.5rem;
  padding: 0.4rem 0.6rem;
  background: rgb(239 68 68 / 0.1);
  border: 1px solid rgb(239 68 68 / 0.3);
  border-radius: 4px;
  color: rgb(252 165 165);
  font-size: 0.8rem;
}
.hint { font-size: 0.8rem; color: var(--muted-foreground); margin-bottom: 0.5rem; }
.matchers-section { border-top: 1px solid var(--border); padding-top: 0.75rem; }
.matchers-section h3 { font-size: 0.85rem; font-weight: 600; margin-bottom: 0.5rem; }
.matcher-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.3rem 0;
  font-size: 0.8rem;
}
.matcher-text { color: var(--muted-foreground); }
.placeholder { font-size: 0.8rem; color: var(--muted-foreground); }
</style>
