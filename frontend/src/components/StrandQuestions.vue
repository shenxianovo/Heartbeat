<script setup lang="ts">
import { ref, watch } from 'vue'
import { fetchDailyQuestions, bindStrand, muteHandle, type QuestionItem } from '../api/index'
import { Card } from '@/components/ui/card'

/**
 * Strand 提问面板（ADR-028 §4/§5，单锚点重构）：owner-only。
 * 每天封顶 1–3 个问题，每个问题针对**一个**高特异性把手："huasheng.cn 是什么？"。
 * 共现把手只作提示文案。三出口：入库 / 别再问（Mute）/ 跳过（纯客户端，下次 diff 再端上来）。
 */
const props = defineProps<{ selectedDate: string }>()

interface Draft {
  q: QuestionItem
  name: string
  gloss: string
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
  return m >= 60 ? `${Math.floor(m / 60)}小时${m % 60 > 0 ? `${m % 60}分` : ''}` : `${m}分钟`
}

async function submit(d: Draft) {
  if (!d.name.trim()) return
  d.busy = true
  try {
    await bindStrand({ name: d.name.trim(), gloss: d.gloss.trim(), members: [d.q.anchor] })
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
</script>

<template>
  <Card v-if="drafts.length > 0" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-4 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">
        认识一下 · {{ drafts.length }} 个没见过的东西
      </h2>

      <div
        v-for="d in drafts"
        :key="d.q.anchor.source + '/' + d.q.anchor.token"
        class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/40 p-4"
      >
        <p class="text-[0.92rem]">
          <span class="font-semibold">{{ d.q.anchor.token }}</span>
          <span class="text-muted-foreground"> · 今天 {{ minutes(d.q.totalSeconds) }}，这是什么？</span>
        </p>

        <p v-if="d.q.handles.length > 0" class="text-[0.78rem] text-muted-foreground/70">
          同时段还有：{{ d.q.handles.map(h => h.token).join('、') }}
        </p>

        <div class="flex flex-col gap-2">
          <input
            v-model="d.name"
            type="text"
            placeholder="给它一个名字（如 花生）"
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
            :disabled="d.busy || !d.name.trim()"
            @click="submit(d)"
          >入库</button>
        </div>
      </div>
    </div>
  </Card>
</template>
