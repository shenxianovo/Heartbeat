<script setup lang="ts">
import { ref, watch } from 'vue'
import { fetchDailyQuestions, bindStrand, muteMatcher, type IQuestionItemResponse, type IMatcherDto } from '../api/index'
import { Card } from '@/components/ui/card'

/**
 * 发问面板（ADR-029 §4/§5）：owner-only。判官每天最多端上 3 张问题卡，
 * 每张锚定一个 Matcher 提案（观测指纹）+ AI 的一次性名字/释义提案。
 * 三出口：入库（绑定 Strand）/ 别再问（Mute Matcher）/ 跳过（纯客户端，下次 diff 再端上来）。
 * 策展纪律：指纹只收特异性标识；通用工具写进释义，不进指纹。
 */
const props = defineProps<{ selectedDate: string }>()

interface Draft {
  q: IQuestionItemResponse
  name: string
  gloss: string
  busy: boolean
}

const drafts = ref<Draft[]>([])

/** 归入既有脉络的轻提示（新建保持静默）：让"指纹在长"可感知，也是撞错名的唯一发现信号。 */
const notice = ref<string | null>(null)
let noticeTimer: ReturnType<typeof setTimeout> | undefined

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

const OP_LABEL: Record<string, string> = { equals: '=', prefix: '开头是', contains: '含' }
const READING_LABEL: Record<string, string> = { app: '应用', title: '窗口标题', url: '网址', tab_title: '标签页' }

/** Matcher 的人类可读渲染：`应用 = "livehime" 且 窗口标题 含 "直播"`。 */
function describeMatcher(m: IMatcherDto | undefined): string {
  if (!m?.steps?.length) return ''
  return m.steps
    .map(s => `${READING_LABEL[s.reading ?? ''] ?? s.reading} ${OP_LABEL[s.op ?? ''] ?? s.op} “${s.value}”`)
    .join(' 且 ')
}

function draftKey(d: Draft): string {
  return JSON.stringify(d.q.matcher ?? {})
}

async function submit(d: Draft) {
  if (!d.name.trim() || !d.q.matcher) return
  d.busy = true
  try {
    const res = await bindStrand({ name: d.name.trim(), gloss: d.gloss.trim(), members: [d.q.matcher] })
    // createdAt < updatedAt ⇔ 服务端归入了既有 Strand（指纹并集追加，见 KnowledgeService）
    if (res.createdAt && res.updatedAt && res.createdAt.getTime() < res.updatedAt.getTime()) {
      notice.value = `已归入既有脉络「${res.name}」 · 指纹 ${res.members?.length ?? 0} 条`
      clearTimeout(noticeTimer)
      noticeTimer = setTimeout(() => { notice.value = null }, 5000)
    }
    remove(d)
  } catch {
    d.busy = false
  }
}

async function mute(d: Draft) {
  if (!d.q.matcher) return
  d.busy = true
  try {
    await muteMatcher(d.q.matcher) // 静音这个观测指纹：别再问
    remove(d)
  } catch {
    d.busy = false
  }
}
</script>

<template>
  <Card v-if="drafts.length > 0 || notice" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-4 px-5">
      <h2 v-if="drafts.length > 0" class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">
        认识一下 · {{ drafts.length }} 个说不清的活动
      </h2>

      <p v-if="notice" class="text-[0.8rem] text-muted-foreground">✓ {{ notice }}</p>

      <div
        v-for="d in drafts"
        :key="draftKey(d)"
        class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/40 p-4"
      >
        <p class="text-[0.92rem]">{{ d.q.question }}</p>

        <p v-if="d.q.evidence" class="text-[0.78rem] text-muted-foreground/70">
          依据：{{ d.q.evidence }}
        </p>

        <p class="text-[0.78rem] text-muted-foreground/70">
          指纹：<span class="font-mono">{{ describeMatcher(d.q.matcher) }}</span>
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
            placeholder="一句话说明这是什么（常一起开的通用工具也写在这里）"
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
