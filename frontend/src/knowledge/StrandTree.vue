<script setup lang="ts">
import { ref } from 'vue'
import type { StrandTreeNode } from './useStrandTree'
import type { ICreateStrandRequest } from '../api/index'

const props = defineProps<{
  tree: StrandTreeNode[]
  selectedId: string | null
  expandedIds: Set<string>
  loading: boolean
  error: string | null
}>()

const emit = defineEmits<{
  (e: 'select', id: string): void
  (e: 'toggle', id: string): void
  (e: 'create', req: ICreateStrandRequest): void
}>()

const showCreateForm = ref(false)
const newName = ref('')
const newGloss = ref('')

function hasChildren(node: StrandTreeNode): boolean {
  return node.children.length > 0
}

function submitCreate() {
  if (!newName.value.trim()) return
  emit('create', {
    name: newName.value.trim(),
    gloss: newGloss.value.trim(),
  })
  newName.value = ''
  newGloss.value = ''
  showCreateForm.value = false
}

function flatNodes(): { node: StrandTreeNode; ancestors: string[] }[] {
  const result: { node: StrandTreeNode; ancestors: string[] }[] = []
  function walk(nodes: StrandTreeNode[], ancestors: string[]) {
    for (const n of nodes) {
      result.push({ node: n, ancestors })
      if (props.expandedIds.has(n.strand.id!)) {
        walk(n.children, [...ancestors, n.strand.id!])
      }
    }
  }
  walk(props.tree, [])
  return result
}

function formatDateRange(strand: { startedOn?: Date | null; endedOn?: Date | null }): string {
  const s = strand.startedOn ? formatDate(strand.startedOn) : '…'
  const e = strand.endedOn ? formatDate(strand.endedOn) : '至今'
  if (s === '…' && e === '至今') return ''
  return `${s} ~ ${e}`
}

function formatDate(d: Date): string {
  if (!(d instanceof Date) || isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}
</script>

<template>
  <div class="strand-tree">
    <div class="tree-header">
      <span class="tree-title">脉络树</span>
      <button class="btn-sm" @click="showCreateForm = !showCreateForm">
        {{ showCreateForm ? '取消' : '新建' }}
      </button>
    </div>

    <div v-if="showCreateForm" class="create-form">
      <input v-model="newName" placeholder="名称" class="input" @keydown.enter="submitCreate" />
      <input v-model="newGloss" placeholder="释义（可选）" class="input" />
      <button class="btn-sm primary" :disabled="!newName.trim()" @click="submitCreate">创建</button>
    </div>

    <p v-if="loading" class="placeholder">加载中…</p>
    <p v-else-if="error" class="error-text">{{ error }}</p>
    <p v-else-if="tree.length === 0" class="placeholder">暂无脉络</p>

    <div v-else class="tree-list">
      <div
        v-for="{ node } in flatNodes()"
        :key="node.strand.id"
        class="tree-node"
        :class="{
          selected: node.strand.id === selectedId,
          ended: !!node.strand.endedOn,
        }"
        :style="{ paddingLeft: `${node.depth * 1.2 + 0.5}rem` }"
        @click="emit('select', node.strand.id!)"
      >
        <button
          v-if="hasChildren(node)"
          class="expand-btn"
          @click.stop="emit('toggle', node.strand.id!)"
        >
          {{ expandedIds.has(node.strand.id!) ? '▾' : '▸' }}
        </button>
        <span v-else class="expand-placeholder"></span>
        <span class="node-name">{{ node.strand.name }}</span>
        <span v-if="node.strand.members?.length" class="matcher-pill">
          {{ node.strand.members.length }}
        </span>
        <span v-if="formatDateRange(node.strand)" class="date-badge">
          {{ formatDateRange(node.strand) }}
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.strand-tree {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 0.75rem;
  max-height: 70vh;
  overflow-y: auto;
}
.tree-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
}
.tree-title { font-weight: 600; font-size: 0.9rem; }
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
.btn-sm:disabled { opacity: 0.5; cursor: default; }
.create-form {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  margin-bottom: 0.75rem;
  padding: 0.5rem;
  background: var(--card);
  border-radius: 6px;
}
.input {
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--border);
  border-radius: 4px;
  background: transparent;
  color: var(--foreground);
  font-size: 0.8rem;
}
.placeholder { color: var(--muted-foreground); font-size: 0.85rem; }
.error-text { color: rgb(239 68 68); font-size: 0.85rem; }
.tree-list { display: flex; flex-direction: column; }
.tree-node {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  padding: 0.4rem 0.5rem;
  border-radius: 4px;
  cursor: pointer;
  font-size: 0.85rem;
}
.tree-node:hover { background: var(--card); }
.tree-node.selected { background: var(--card); outline: 1px solid var(--border); }
.tree-node.ended { opacity: 0.5; }
.expand-btn {
  background: none;
  border: none;
  color: var(--muted-foreground);
  cursor: pointer;
  padding: 0;
  width: 1rem;
  text-align: center;
  font-size: 0.75rem;
}
.expand-placeholder { width: 1rem; }
.node-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.matcher-pill {
  font-size: 0.65rem;
  background: var(--primary);
  color: var(--primary-foreground, #fff);
  padding: 0.1rem 0.35rem;
  border-radius: 999px;
  line-height: 1;
}
.date-badge {
  font-size: 0.65rem;
  color: var(--muted-foreground);
  white-space: nowrap;
}
</style>
