<script setup lang="ts">
import { ref, watch } from 'vue'
import { fetchDailyQuestions, bindStrand, muteHandle, type QuestionItem, type HandleDto } from '../api/index'
import { Card } from '@/components/ui/card'

/**
 * Strand 提问面板（ADR-028 §4/§5）：owner-only。
 * 每天封顶 1–3 个"疑惑簇"，AI 给一次性提案，用户表单纠错后三出口：提交 / Mute / 跳过。
 * 跳过纯客户端（下次 diff 自然再端上来）；提交/ Mute 落库。
 */
const props = defineProps<{ selectedDate: string }>()

interface Draft {
  q: QuestionItem
  name: string
  gloss: string
  selected: boolean[]
  busy: boolean
}

const drafts = ref<Draft[]>([])

async function load() {
  try {
    const res = await fetchDailyQuestions({ date: props.selectedDate })
    drafts.value = (res.questions ?? []).map(q => ({
      q,
      name: q.proposedName ?? '',
      gloss: q.proposedGloss ?? '',
      selected: q.handles.map(() => true),
      busy: false,
    }))
  } catch {
    drafts.value = [] // 提问是可选增强，取数失败静默不打扰
  }
}

watch(() => props.selectedDate, load, { immediate: true })

function remove(d: Draft) {
  drafts.value = drafts.value.filter(x => x !== d)
}

function minutes(seconds: number): string {
  const m = Math.round(seconds / 60)
  return m >= 60 ? `${Math.floor(m / 60)}小时${m % 60}分` : `${m}分`
}

async function submit(d: Draft) {
  const members = d.q.handles.filter((_, i) => d.selected[i])
  if (!d.name.trim() || members.length === 0) return
  d.busy = true
  try {
    await bindStrand({ name: d.name.trim(), gloss: d.gloss.trim(), members })
    remove(d)
  } catch {
    d.busy = false
  }
}

async function mute(d: Draft) {
  d.busy = true
  try {
    await muteHandle(d.q.anchor) // 静音锚点把手：别再问
    remove(d)
  } catch {
    d.busy = false
  }
}

function handleName(h: HandleDto): string {
  return h.token
}
</script>

<template>
  <Card v-if="drafts.length > 0" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-4 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">
        AI 有 {{ drafts.length }} 个问题 · 这些是什么？
      </h2>

      <div
        v-for="d in drafts"
        :key="d.q.anchor.source + '/' + d.q.anchor.token"
        class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/40 p-4"
      >
        <p class="text-[0.8rem] text-muted-foreground">
          今天这些一起出现了约 {{ minutes(d.q.totalSeconds) }}，是一件事吗？是什么？
        </p>

        <div class="flex flex-wrap gap-2">
          <label
            v-for="(h, i) in d.q.handles"
            :key="h.source + '/' + h.token"
            class="flex cursor-pointer items-center gap-1.5 rounded-md border border-border/50 px-2 py-1 text-[0.78rem]"
            :class="d.selected[i] ? 'text-foreground' : 'text-muted-foreground/60 line-through'"
          >
            <input v-model="d.selected[i]" type="checkbox" class="accent-current" />
            {{ handleName(h) }}
          </label>
        </div>

        <div class="flex flex-col gap-2">
          <input
            v-model="d.name"
            type="text"
            placeholder="给它起个名字（如 HyperFrames）"
            class="w-full rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.9rem] outline-none focus:border-border"
          />
          <input
            v-model="d.gloss"
            type="text"
            placeholder="一句话说明这是什么（可留空）"
            class="w-full rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.85rem] outline-none focus:border-border"
          />
        </div>

        <div class="flex items-center justify-end gap-2">
          <button
            class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
            :disabled="d.busy"
            title="下次可能还会问"
            @click="remove(d)"
          >跳过</button>
          <button
            class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
            :disabled="d.busy"
            title="别再问这个"
            @click="mute(d)"
          >别再问</button>
          <button
            class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors hover:text-foreground disabled:opacity-50"
            :disabled="d.busy || !d.name.trim() || d.selected.every(s => !s)"
            @click="submit(d)"
          >入库</button>
        </div>
      </div>
    </div>
  </Card>
</template>
