<script setup lang="ts">
import { ref, watch } from 'vue'
import {
  proposeCorrection, commitChangeSet, fetchStrands,
  toApiError, changeSetErrorOf, knowledgeErrorOf,
  type IKnowledgeProposalResponse, type IStrandResponse,
} from '../api/index'
import {
  toReviewItems, selectedOps, canCommit, interpretCorrectionError, type ReviewItem,
} from '../teaching/teachingFlow'
import {
  submitCorrection, retryRegenerate, correctionStageHint,
  type CorrectionStage, type CorrectionOutcome,
} from '../teaching/correctionFlow'
import ProposalReview from './ProposalReview.vue'
import type { CalendarContext } from '../calendar/localCalendarWindow'

/**
 * Recap 纠正面板（ADR-031 §6，issue 06）：owner-only。用户对这一天的回顾说哪里不对 →
 * 服务端整理成可编辑 KnowledgeChangeSet（零写入）→ 逐项确认 → 事务提交 → 提交成功后
 * 由父组件对这一天做一次显式重生成（走 POST 的 SSE 流，ADR-042 §2）。纠正写的是知识，
 * 不是散文补丁；生成失败不回滚知识，也不覆盖上一版成功 Recap。
 */
const props = defineProps<{
  calendarContext: CalendarContext
  /** 目标日的显式流式重生成；失败必须 reject（生成失败与提交失败语义不同）。 */
  regenerate: (calendarContext: CalendarContext) => Promise<void>
}>()

const stage = ref<CorrectionStage>('closed')
const correction = ref('')
const proposal = ref<IKnowledgeProposalResponse | null>(null)
const items = ref<ReviewItem[]>([])
const strands = ref<IStrandResponse[]>([])
const readingLabels = ref<Record<string, string>>({})
const error = ref('')
const failedOpId = ref<string | null>(null)
const conflict = ref(false)
const summary = ref<string[]>([])

/** 新 refresh generation：纠正必须重新绑定当前不可变 Calendar Context。 */
watch(() => props.calendarContext.correlationIdentity, () => reset())

function reset() {
  stage.value = 'closed'
  correction.value = ''
  proposal.value = null
  items.value = []
  error.value = ''
  failedOpId.value = null
  conflict.value = false
  summary.value = []
}

function open() {
  stage.value = 'writing'
  error.value = ''
}

/** 第一阶段：整理成提案。零写入——失败可原样重试。 */
async function propose() {
  if (!correction.value.trim()) return
  const expectedIdentity = props.calendarContext.correlationIdentity
  stage.value = 'proposing'
  error.value = ''
  failedOpId.value = null
  conflict.value = false
  try {
    const [res, strandList] = await Promise.all([
      proposeCorrection({ window: props.calendarContext.day, correction: correction.value }),
      fetchStrands().catch(() => [] as IStrandResponse[]),
    ])
    if (props.calendarContext.correlationIdentity !== expectedIdentity) return
    proposal.value = res
    items.value = toReviewItems(res)
    strands.value = strandList
    readingLabels.value = { ...readingLabels.value, ...(res.readingLabels ?? {}) }
    stage.value = 'review'
  } catch (e) {
    if (props.calendarContext.correlationIdentity !== expectedIdentity) return
    error.value = interpretCorrectionError(toApiError(e), knowledgeErrorOf(e)?.code).message
    stage.value = 'writing' // 回到可编辑态：用户的原话不丢
  }
}

/** 第二阶段：提交 → 仅在提交成功后重生成这一天。 */
async function commit() {
  const expectedContext = props.calendarContext
  stage.value = 'committing'
  error.value = ''
  failedOpId.value = null
  const outcome = await submitCorrection(selectedOps(items.value), {
    commit: ops => commitChangeSet(ops),
    regenerate: () => props.regenerate(expectedContext),
    toApiError,
    changeSetErrorOf,
  })
  if (props.calendarContext.correlationIdentity !== expectedContext.correlationIdentity) return
  apply(outcome)
}

async function retry() {
  const expectedContext = props.calendarContext
  stage.value = 'committing'
  const outcome = await retryRegenerate(summary.value, () => props.regenerate(expectedContext))
  if (props.calendarContext.correlationIdentity !== expectedContext.correlationIdentity) return
  apply(outcome)
}

function apply(outcome: CorrectionOutcome) {
  if (outcome.kind === 'commitFailed') {
    error.value = outcome.failure.message
    failedOpId.value = outcome.failure.failedOpId
    conflict.value = outcome.failure.conflict
    stage.value = 'review' // 保留用户编辑，供修复后重提
    return
  }
  summary.value = outcome.summary
  if (outcome.kind === 'regenerateFailed') {
    error.value = outcome.message
    stage.value = 'regenerateFailed'
    return
  }
  error.value = ''
  stage.value = 'done'
}
</script>

<template>
  <div class="flex flex-col gap-2 border-t border-border/40 pt-3">
    <!-- 入口：只在私有视图出现（父组件保证 owner-only） -->
    <div v-if="stage === 'closed'" class="flex justify-end">
      <button
        class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground"
        title="告诉 Heartbeat 这一天哪里理解错了——纠正会写进知识,这一天的回顾随后重新生成"
        @click="open"
      >这里不对</button>
    </div>

    <!-- Stage 1：自然语言纠正 -->
    <template v-else-if="stage === 'writing' || stage === 'proposing'">
      <p class="text-[0.78rem] text-muted-foreground/80">
        说说这一天哪里不对：遗漏了什么、关联错了什么,或者有什么私人语境希望 Heartbeat 长期记住。
      </p>
      <textarea
        v-model="correction"
        rows="3"
        :disabled="stage === 'proposing'"
        placeholder="例如：下午那段不是在写代码,是在给 Hyperframes 做产品调研"
        class="w-full resize-y rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.85rem] outline-none focus:border-border disabled:opacity-60"
      ></textarea>
      <p v-if="error" class="text-[0.78rem] text-destructive">{{ error }}</p>
      <div class="flex items-center justify-end gap-2">
        <button
          class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
          :disabled="stage === 'proposing'"
          @click="reset"
        >取消</button>
        <button
          class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors disabled:opacity-50"
          :disabled="stage === 'proposing' || !correction.trim()"
          @click="propose"
        >{{ stage === 'proposing' ? '整理中…' : '整理成变更' }}</button>
      </div>
      <p v-if="stage === 'proposing'" class="text-[0.72rem] text-muted-foreground/60">{{ correctionStageHint(stage) }}</p>
    </template>

    <!-- Stage 2：提案审阅 -->
    <template v-else-if="stage === 'review' || stage === 'committing'">
      <p v-if="items.length === 0" class="text-[0.85rem] text-muted-foreground">
        这次没有需要保存的变更。可以取消,或回去把纠正说得更具体。
      </p>
      <ProposalReview
        :proposal="proposal"
        :items="items"
        :strands="strands"
        :reading-labels="readingLabels"
        :locked="stage === 'committing'"
        :failed-op-id="failedOpId"
      />
      <p v-if="error" class="text-[0.78rem] text-destructive">{{ error }}</p>
      <p v-if="stage === 'committing'" class="text-[0.72rem] text-muted-foreground/60">{{ correctionStageHint(stage) }}</p>
      <div class="flex items-center justify-end gap-2">
        <button
          class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
          :disabled="stage === 'committing'"
          @click="reset"
        >取消</button>
        <button
          v-if="conflict"
          class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground disabled:opacity-50"
          :disabled="stage === 'committing'"
          title="基于最新知识状态重新整理提案,再重新审阅"
          @click="propose"
        >重新加载最新知识并重新审阅</button>
        <button
          class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
          :disabled="stage === 'committing'"
          title="回去修改纠正(当前提案会丢弃)"
          @click="stage = 'writing'"
        >返回修改</button>
        <button
          class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors disabled:opacity-50"
          :disabled="stage === 'committing' || !canCommit(items)"
          @click="commit"
        >{{ stage === 'committing' ? '保存并重新生成…' : `确认保存 ${selectedOps(items).length} 项` }}</button>
      </div>
    </template>

    <!-- 知识已保存但重生成失败：不覆盖上一版回顾,单独重试 -->
    <template v-else-if="stage === 'regenerateFailed'">
      <ul class="flex flex-col gap-0.5">
        <li v-for="(s, i) in summary" :key="i" class="text-[0.8rem] text-muted-foreground">✓ {{ s }}</li>
      </ul>
      <p class="text-[0.78rem] text-amber-600 dark:text-amber-500">{{ error }}</p>
      <div class="flex items-center justify-end gap-2">
        <button
          class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground"
          @click="reset"
        >稍后再说</button>
        <button class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground" @click="retry">重试生成</button>
      </div>
    </template>

    <!-- 完成：知识已保存 + 这一天已重新生成 -->
    <template v-else-if="stage === 'done'">
      <ul class="flex flex-col gap-0.5">
        <li v-for="(s, i) in summary" :key="i" class="text-[0.8rem] text-muted-foreground">✓ {{ s }}</li>
      </ul>
      <p class="text-[0.78rem] text-muted-foreground/70">这一天的回顾已用更新后的知识重新生成。</p>
      <div class="flex justify-end">
        <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground" @click="reset">好</button>
      </div>
    </template>
  </div>
</template>
