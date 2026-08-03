<script setup lang="ts">
import { ref, watch } from 'vue'
import {
  fetchDailyQuestions, fetchStrands, proposeFromQuestion, commitChangeSet, muteMatcher,
  toApiError, changeSetErrorOf, knowledgeErrorOf, StrandRefDto, KnowledgeOperationDto,
  type IAskingQuestionResponse, type IKnowledgeProposalResponse, type IStrandResponse,
} from '../api/index'
import { formatDuration } from '../composables/useHeartbeat'
import {
  toReviewItems, selectedOps, groupByCategory, precheck, canCommit,
  strandRefOptions, strandDisplay, episodeRefDisplay, rebindMatcherTarget,
  refToValue, valueToRef, dateToInput, inputToDate, dateOnlyLabel,
  describeMatcher, formatTimeRange, isRecurrence,
  interpretProposeError, interpretCommitError, commitSummary, OP_META,
  type ReviewItem,
} from '../teaching/teachingFlow'
import { Card } from '@/components/ui/card'

/**
 * 主动教学两阶段面板（ADR-031 §6，issue 05）：owner-only。
 * 证据卡（真实活动簇的时段 + 跨 Source 观察）→ 用户自然语言解释 → LLM 整理成可逐项
 * 编辑/取消的 KnowledgeChangeSet → 显式确认 → 事务提交。提案阶段零写入，只有最终
 * 确认才调 commit。跳过纯客户端；静音按确认后的 Mute 语义提交（cluster 静音指纹，
 * recurrence 解决探针），不隐藏原始 Observation。
 */
const props = defineProps<{ selectedDate: string }>()

type Stage = 'evidence' | 'proposing' | 'review' | 'committing' | 'done'

interface TeachingCard {
  q: IAskingQuestionResponse
  stage: Stage
  answer: string
  proposal: IKnowledgeProposalResponse | null
  items: ReviewItem[]
  /** 当前阶段的人话错误；提交失败不清空用户编辑。 */
  error: string | null
  /** 证据已过期（服务端 404）：只能刷新问题列表，不能带着旧证据继续。 */
  expired: boolean
  /** commit 验证失败定位到的操作。 */
  failedOpId: string | null
  /** 409 并发冲突：提供重新整理提案（刷新版本）路径，不静默覆盖。 */
  conflict: boolean
  muteConfirm: boolean
  busy: boolean
  summary: string[]
}

const cards = ref<TeachingCard[]>([])
const readingLabels = ref<Record<string, string>>({})
const strands = ref<IStrandResponse[]>([])

async function load() {
  try {
    const res = await fetchDailyQuestions({ date: props.selectedDate })
    readingLabels.value = res.readingLabels ?? {}
    cards.value = (res.questions ?? []).map(q => ({
      q,
      stage: 'evidence' as Stage,
      answer: '',
      proposal: null,
      items: [],
      error: null,
      expired: false,
      failedOpId: null,
      conflict: false,
      muteConfirm: false,
      busy: false,
      summary: [],
    }))
  } catch {
    cards.value = [] // 提问是可选增强，取数失败静默不打扰
  }
}

watch(() => props.selectedDate, load, { immediate: true })

function remove(c: TeachingCard) {
  cards.value = cards.value.filter(x => x !== c)
}

// ===== Stage 1 → 2：自然语言回答换提案（零写入） =====

async function propose(c: TeachingCard) {
  if (!c.answer.trim() || !c.q.id) return
  c.stage = 'proposing'
  c.error = null
  c.conflict = false
  c.failedOpId = null
  try {
    const [proposal] = await Promise.all([
      proposeFromQuestion(c.q.id, { date: props.selectedDate, answer: c.answer }),
      loadStrands(),
    ])
    c.proposal = proposal
    c.items = toReviewItems(proposal)
    // 提案可能引入证据卡之外的读数（如 LLM 引用了别的 Source 指纹）:标签词典做并集
    readingLabels.value = { ...readingLabels.value, ...(proposal.readingLabels ?? {}) }
    c.stage = 'review'
  } catch (e) {
    c.stage = 'evidence' // 回答保留，可修改后重试
    const failure = interpretProposeError(toApiError(e), knowledgeErrorOf(e)?.code)
    c.expired = failure.expired
    c.error = failure.message
  }
}

/** 已有 Strand 树（path/日期/版本）：review 里"选择已有节点"的消歧数据源。失败不挡 review。 */
async function loadStrands() {
  try {
    strands.value = await fetchStrands()
  } catch {
    // 列表加载失败时,已有节点只能按 Id 展示;不阻塞主流程
  }
}

// ===== Stage 2 → commit：只有显式确认才写入 =====

async function commit(c: TeachingCard) {
  const ops = selectedOps(c.items)
  if (ops.length === 0) return
  c.stage = 'committing'
  c.error = null
  c.failedOpId = null
  c.conflict = false
  try {
    const res = await commitChangeSet(ops)
    c.summary = commitSummary(res.results ?? [])
    c.stage = 'done'
  } catch (e) {
    c.stage = 'review' // 用户编辑内容原样保留
    const failure = interpretCommitError(changeSetErrorOf(e), toApiError(e))
    c.failedOpId = failure.failedOpId
    c.conflict = failure.conflict
    c.error = failure.message
  }
}

/** 并发冲突出口：基于最新知识重新整理提案（版本重新盖章），再走一遍审阅。 */
async function reproposeAfterConflict(c: TeachingCard) {
  await propose(c)
}

// ===== 静音：按确认后的 Mute 语义提交 =====

async function confirmMute(c: TeachingCard) {
  c.busy = true
  c.error = null
  try {
    if (isRecurrence(c.q) && c.q.probeId) {
      // recurrence 的"别再问"= 解决探针为 muted（静音指纹不会停掉活跃 Probe）
      await commitChangeSet([KnowledgeOperationDto.fromJS({
        opId: 'op1', type: 'resolveProbe', resolveProbe: { probeId: c.q.probeId, resolution: 'muted' },
      })])
    } else if (c.q.matcher) {
      await muteMatcher(c.q.matcher)
    }
    remove(c)
  } catch {
    c.busy = false
    c.muteConfirm = false
    c.error = '静音失败，请重试'
  }
}

/** 回到第一阶段修改回答：当前提案作废（重新整理会全量覆盖），编辑不保留。 */
function backToAnswer(c: TeachingCard) {
  c.stage = 'evidence'
  c.error = null
  c.conflict = false
  c.failedOpId = null
}

// ===== review 编辑的模板小助手（生成 client 的字段带索引签名,可安全按名读写） =====

function setDateField(target: Record<string, unknown> | undefined, field: string, ev: Event) {
  if (target) target[field] = inputToDate((ev.target as HTMLInputElement).value)
}

function setStrandRef(target: Record<string, unknown> | undefined, field: string, ev: Event) {
  if (!target) return
  const parsed = valueToRef((ev.target as HTMLSelectElement).value)
  target[field] = parsed ? StrandRefDto.fromJS(parsed) : undefined
}

/** bindMatcher 换绑：expectedVersion 必须跟着目标走（见 rebindMatcherTarget）。 */
function setBindTarget(item: ReviewItem, ev: Event) {
  const parsed = valueToRef((ev.target as HTMLSelectElement).value)
  if (parsed) rebindMatcherTarget(item.op, parsed, strands.value)
}

const inputCls = 'w-full rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.85rem] outline-none focus:border-border'
const selectCls = 'w-full rounded-md border border-border/50 bg-background/60 px-2 py-1.5 text-[0.85rem] outline-none focus:border-border'
const labelCls = 'text-[0.72rem] text-muted-foreground/80'
</script>

<template>
  <Card v-if="cards.length > 0" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-4 px-5">
      <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">
        认识一下 · {{ cards.length }} 个说不清的活动
      </h2>

      <div
        v-for="c in cards"
        :key="c.q.id ?? ''"
        class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/40 p-4"
      >
        <!-- ===== 证据卡:系统观察到的活动,不是已确定的归属 ===== -->
        <p class="text-[0.92rem]">{{ c.q.question }}</p>

        <div v-if="isRecurrence(c.q)" class="rounded-md border border-border/40 bg-background/50 px-3 py-2 text-[0.8rem] text-muted-foreground">
          上次记录：「{{ c.q.episodeText }}」（{{ c.q.episodeDate ? dateOnlyLabel(c.q.episodeDate) : '' }}）
        </div>

        <div class="flex flex-col gap-1.5">
          <p class="text-[0.72rem] text-muted-foreground/60">
            系统观察到的活动<template v-if="formatTimeRange(c.q.approximateStart, c.q.approximateEnd)">（{{ formatTimeRange(c.q.approximateStart, c.q.approximateEnd) }} 前后）</template>——归属由你决定：
          </p>
          <ul class="flex flex-col gap-0.5">
            <li
              v-for="(o, i) in c.q.observations ?? []"
              :key="i"
              class="flex items-baseline gap-2 text-[0.8rem]"
              :class="o.matchesFingerprint ? 'text-foreground' : 'text-muted-foreground/70'"
            >
              <span class="shrink-0 font-mono text-[0.7rem] text-muted-foreground/50">{{ o.source }}</span>
              <span class="min-w-0 truncate">{{ o.value }}<template v-if="o.detail"> · {{ o.detail }}</template></span>
              <span class="ml-auto shrink-0 font-mono text-[0.7rem] text-muted-foreground/50">{{ formatDuration(o.seconds ?? 0) }}</span>
            </li>
          </ul>
          <p class="text-[0.72rem] text-muted-foreground/50">
            指纹：<span class="font-mono">{{ describeMatcher(c.q.matcher, readingLabels) }}</span>
          </p>
        </div>

        <!-- ===== Stage 1:自然语言回答 ===== -->
        <template v-if="c.stage === 'evidence' || c.stage === 'proposing'">
          <textarea
            v-model="c.answer"
            rows="2"
            :disabled="c.stage === 'proposing'"
            placeholder="用你自己的话说说这是什么——一次性的事、持续的脉络、属于哪个已有语境,或者还不确定,都可以直接写"
            class="w-full resize-y rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.9rem] outline-none focus:border-border disabled:opacity-50"
          ></textarea>

          <p v-if="c.error" class="text-[0.78rem] text-destructive">{{ c.error }}</p>

          <div v-if="c.muteConfirm" class="flex items-center justify-between gap-2 rounded-md border border-border/40 bg-background/50 px-3 py-2">
            <span class="text-[0.78rem] text-muted-foreground">确认后不再就这个{{ isRecurrence(c.q) ? '探针' : '指纹' }}发问；原始活动记录不受影响，仍会如实出现在回顾里。</span>
            <div class="flex shrink-0 gap-2">
              <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground hover:text-foreground" :disabled="c.busy" @click="c.muteConfirm = false">取消</button>
              <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground disabled:opacity-50" :disabled="c.busy" @click="confirmMute(c)">确认静音</button>
            </div>
          </div>

          <div class="flex items-center justify-end gap-2">
            <button
              v-if="c.expired"
              class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground"
              @click="load()"
            >刷新问题</button>
            <button
              class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
              :disabled="c.stage === 'proposing'"
              title="不写入任何内容,下次可能还会问"
              @click="remove(c)"
            >跳过</button>
            <button
              class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
              :disabled="c.stage === 'proposing' || c.muteConfirm"
              title="别再问这个"
              @click="c.muteConfirm = true"
            >别再问</button>
            <button
              class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors disabled:opacity-50"
              :disabled="c.stage === 'proposing' || !c.answer.trim() || c.expired"
              @click="propose(c)"
            >{{ c.stage === 'proposing' ? '整理中…' : '整理成变更' }}</button>
          </div>
          <p v-if="c.stage === 'proposing'" class="text-[0.72rem] text-muted-foreground/60">
            正在把你的解释整理成结构化变更——这一步不会写入任何知识,整理好后由你逐项确认。
          </p>
        </template>

        <!-- ===== Stage 2:提案审阅(逐项编辑/取消,确认后才提交) ===== -->
        <template v-else-if="c.stage === 'review' || c.stage === 'committing'">
          <div class="flex flex-col gap-3 border-t border-border/40 pt-3">
            <p v-if="c.proposal?.explanation" class="text-[0.85rem] text-muted-foreground">{{ c.proposal.explanation }}</p>

            <ul v-if="(c.proposal?.warnings ?? []).length > 0" class="flex flex-col gap-0.5">
              <li v-for="(w, i) in c.proposal!.warnings" :key="i" class="text-[0.75rem] text-amber-600 dark:text-amber-500">⚠ {{ w }}</li>
            </ul>
            <ul v-if="(c.proposal?.suggestions ?? []).length > 0" class="flex flex-col gap-0.5">
              <li v-for="(s, i) in c.proposal!.suggestions" :key="i" class="text-[0.75rem] text-muted-foreground/70">💡 {{ s }}（无需保存）</li>
            </ul>

            <p v-if="c.items.length === 0" class="text-[0.85rem] text-muted-foreground">
              这次没有需要保存的变更。可以直接关掉,或回去补充说明。
            </p>

            <!-- 分区渲染:脉络 / 指纹 / 片段事实 / 探针 -->
            <div v-for="group in groupByCategory(c.items)" :key="group.category" class="flex flex-col gap-2">
              <h3 class="text-[0.72rem] font-semibold uppercase tracking-[0.06em] text-muted-foreground/70">{{ group.label }}</h3>

              <div
                v-for="item in group.items"
                :key="item.op.opId ?? ''"
                class="flex flex-col gap-2 rounded-md border px-3 py-2.5 transition-colors"
                :class="[
                  c.failedOpId && c.failedOpId === item.op.opId ? 'border-destructive' : 'border-border/40',
                  item.enabled ? 'bg-background/50' : 'bg-background/20 opacity-60',
                ]"
              >
                <div class="flex items-center gap-2">
                  <label class="flex cursor-pointer items-center gap-2 text-[0.82rem] font-medium">
                    <input v-model="item.enabled" type="checkbox" class="accent-foreground" :disabled="c.stage === 'committing'" />
                    {{ OP_META[item.op.type ?? '']?.label ?? item.op.type }}
                  </label>
                  <span v-if="c.failedOpId && c.failedOpId === item.op.opId" class="text-[0.72rem] text-destructive">← 上次提交在这里失败</span>
                </div>

                <template v-if="item.enabled">
                  <!-- createStrand -->
                  <div v-if="item.op.type === 'createStrand' && item.op.createStrand" class="flex flex-col gap-1.5">
                    <input v-model="item.op.createStrand.name" type="text" placeholder="名字" :class="inputCls" />
                    <input v-model="item.op.createStrand.gloss" type="text" placeholder="一句话说明(常一起开的通用工具也写在这里)" :class="inputCls" />
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">父级</span>
                      <select :value="refToValue(item.op.createStrand.parent)" :class="selectCls" @change="setStrandRef(item.op.createStrand, 'parent', $event)">
                        <option v-for="opt in strandRefOptions(c.items, c.items.indexOf(item), strands, '顶层(无父级)')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
                      </select>
                    </div>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">从</span>
                      <input type="date" :value="dateToInput(item.op.createStrand.startedOn)" :class="inputCls" @change="setDateField(item.op.createStrand, 'startedOn', $event)" />
                      <span :class="labelCls">到</span>
                      <input type="date" :value="dateToInput(item.op.createStrand.endedOn)" :class="inputCls" @change="setDateField(item.op.createStrand, 'endedOn', $event)" />
                    </div>
                    <p v-if="(item.op.createStrand.members ?? []).length > 0" :class="labelCls">
                      指纹：<span class="font-mono">{{ (item.op.createStrand.members ?? []).map(m => describeMatcher(m, readingLabels)).join('；') }}</span>
                    </p>
                  </div>

                  <!-- updateStrand -->
                  <div v-else-if="item.op.type === 'updateStrand' && item.op.updateStrand" class="flex flex-col gap-1.5">
                    <p :class="labelCls">编辑：{{ strandDisplay(item.op.updateStrand.strandId, strands) }}</p>
                    <input v-model="item.op.updateStrand.name" type="text" placeholder="名字" :class="inputCls" />
                    <input v-model="item.op.updateStrand.gloss" type="text" placeholder="一句话说明" :class="inputCls" />
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">从</span>
                      <input type="date" :value="dateToInput(item.op.updateStrand.startedOn)" :class="inputCls" @change="setDateField(item.op.updateStrand, 'startedOn', $event)" />
                      <span :class="labelCls">到</span>
                      <input type="date" :value="dateToInput(item.op.updateStrand.endedOn)" :class="inputCls" @change="setDateField(item.op.updateStrand, 'endedOn', $event)" />
                    </div>
                  </div>

                  <!-- moveStrand -->
                  <div v-else-if="item.op.type === 'moveStrand' && item.op.moveStrand" class="flex flex-col gap-1.5">
                    <p :class="labelCls">移动：{{ strandDisplay(item.op.moveStrand.strandId, strands) }}</p>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">新父级</span>
                      <select :value="refToValue(item.op.moveStrand.newParent)" :class="selectCls" @change="setStrandRef(item.op.moveStrand, 'newParent', $event)">
                        <option v-for="opt in strandRefOptions(c.items, c.items.indexOf(item), strands, '顶层(无父级)')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
                      </select>
                    </div>
                    <p class="text-[0.72rem] text-amber-600 dark:text-amber-500">
                      移动是纠错：会改写这条脉络的历史层级解释。如果是现实归属发生了变化,应该结束旧脉络并在新父级下新建,而不是移动。
                    </p>
                  </div>

                  <!-- endStrand -->
                  <div v-else-if="item.op.type === 'endStrand' && item.op.endStrand" class="flex flex-col gap-1.5">
                    <p :class="labelCls">结束：{{ strandDisplay(item.op.endStrand.strandId, strands) }}</p>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">结束于</span>
                      <input type="date" :value="dateToInput(item.op.endStrand.endedOn)" :class="inputCls" @change="setDateField(item.op.endStrand, 'endedOn', $event)" />
                    </div>
                  </div>

                  <!-- bindMatcher -->
                  <div v-else-if="item.op.type === 'bindMatcher' && item.op.bindMatcher" class="flex flex-col gap-1.5">
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">加到</span>
                      <select :value="refToValue(item.op.bindMatcher.strand)" :class="selectCls" @change="setBindTarget(item, $event)">
                        <option v-for="opt in strandRefOptions(c.items, c.items.indexOf(item), strands, null)" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
                      </select>
                    </div>
                    <p :class="labelCls">指纹：<span class="font-mono">{{ describeMatcher(item.op.bindMatcher.matcher, readingLabels) }}</span></p>
                  </div>

                  <!-- muteMatcher -->
                  <div v-else-if="item.op.type === 'muteMatcher' && item.op.muteMatcher" class="flex flex-col gap-1">
                    <p :class="labelCls">静音指纹：<span class="font-mono">{{ describeMatcher(item.op.muteMatcher.matcher, readingLabels) }}</span></p>
                    <p :class="labelCls">只是不再就它发问；原始活动记录仍会如实出现在回顾里。</p>
                  </div>

                  <!-- createEpisode -->
                  <div v-else-if="item.op.type === 'createEpisode' && item.op.createEpisode" class="flex flex-col gap-1.5">
                    <textarea v-model="item.op.createEpisode.text" rows="2" placeholder="当天发生了什么" class="w-full resize-y rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.85rem] outline-none focus:border-border"></textarea>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">日期</span>
                      <input type="date" :value="dateToInput(item.op.createEpisode.localDate)" :class="inputCls" @change="setDateField(item.op.createEpisode, 'localDate', $event)" />
                      <span v-if="item.op.createEpisode.approximateStart" class="shrink-0" :class="labelCls">
                        约 {{ formatTimeRange(item.op.createEpisode.approximateStart, item.op.createEpisode.approximateEnd) }}
                      </span>
                    </div>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">关联脉络</span>
                      <select :value="refToValue(item.op.createEpisode.relatedStrand)" :class="selectCls" @change="setStrandRef(item.op.createEpisode, 'relatedStrand', $event)">
                        <option v-for="opt in strandRefOptions(c.items, c.items.indexOf(item), strands, '不关联(独立存在)')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
                      </select>
                    </div>
                  </div>

                  <!-- updateEpisode -->
                  <div v-else-if="item.op.type === 'updateEpisode' && item.op.updateEpisode" class="flex flex-col gap-1.5">
                    <textarea v-model="item.op.updateEpisode.text" rows="2" class="w-full resize-y rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.85rem] outline-none focus:border-border"></textarea>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">日期</span>
                      <input type="date" :value="dateToInput(item.op.updateEpisode.localDate)" :class="inputCls" @change="setDateField(item.op.updateEpisode, 'localDate', $event)" />
                    </div>
                  </div>

                  <!-- relateEpisode -->
                  <div v-else-if="item.op.type === 'relateEpisode' && item.op.relateEpisode" class="flex flex-col gap-1.5">
                    <p :class="labelCls">片段事实：{{ episodeRefDisplay(item.op.relateEpisode.episode, c.items) }}</p>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">关联到</span>
                      <select :value="refToValue(item.op.relateEpisode.relatedStrand)" :class="selectCls" @change="setStrandRef(item.op.relateEpisode, 'relatedStrand', $event)">
                        <option v-for="opt in strandRefOptions(c.items, c.items.indexOf(item), strands, '解除关联')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
                      </select>
                    </div>
                  </div>

                  <!-- createProbe -->
                  <div v-else-if="item.op.type === 'createProbe' && item.op.createProbe" class="flex flex-col gap-1">
                    <p :class="labelCls">在「{{ episodeRefDisplay(item.op.createProbe.episode, c.items) }}」上留一个探针</p>
                    <p :class="labelCls">谓词：<span class="font-mono">{{ describeMatcher(item.op.createProbe.matcher, readingLabels) }}</span>——再次出现时会来问你是否提升为持续脉络,不会自动写入任何归属。</p>
                  </div>

                  <!-- resolveProbe -->
                  <div v-else-if="item.op.type === 'resolveProbe' && item.op.resolveProbe" class="flex flex-col gap-1">
                    <p :class="labelCls">{{ item.op.resolveProbe.resolution === 'muted' ? '静音这个探针(不再提醒)' : '否认复现(这不是同一件事)' }}——之后不再就它发问。</p>
                  </div>

                  <!-- promoteEpisode -->
                  <div v-else-if="item.op.type === 'promoteEpisode' && item.op.promoteEpisode" class="flex flex-col gap-1.5">
                    <p :class="labelCls">提升：{{ episodeRefDisplay(item.op.promoteEpisode.episode, c.items) }}</p>
                    <div class="flex items-center gap-2">
                      <span :class="labelCls">到脉络</span>
                      <select :value="refToValue(item.op.promoteEpisode.strand)" :class="selectCls" @change="setStrandRef(item.op.promoteEpisode, 'strand', $event)">
                        <option v-for="opt in strandRefOptions(c.items, c.items.indexOf(item), strands, null)" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
                      </select>
                    </div>
                    <p :class="labelCls">非破坏性：原片段事实会保留并关联到该脉络,不是把它转换成脉络。</p>
                  </div>
                </template>
              </div>
            </div>

            <!-- 提交前预检警告(服务端可预知的约束;最终仍以服务端校验为准) -->
            <ul v-if="precheck(c.items).warnings.length > 0" class="flex flex-col gap-0.5">
              <li v-for="(w, i) in precheck(c.items).warnings" :key="i" class="text-[0.75rem] text-amber-600 dark:text-amber-500">⚠ {{ w }}</li>
            </ul>

            <p v-if="c.error" class="text-[0.78rem] text-destructive">{{ c.error }}</p>

            <div class="flex items-center justify-end gap-2">
              <button
                v-if="c.items.length === 0"
                class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground"
                @click="remove(c)"
              >关掉</button>
              <button
                v-if="c.conflict"
                class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-foreground disabled:opacity-50"
                :disabled="c.stage === 'committing'"
                title="基于最新知识状态重新整理提案,再重新审阅"
                @click="reproposeAfterConflict(c)"
              >重新加载最新知识并重新审阅</button>
              <button
                class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:opacity-50"
                :disabled="c.stage === 'committing'"
                title="回去修改回答(当前提案会丢弃)"
                @click="backToAnswer(c)"
              >返回修改回答</button>
              <button
                class="glass-control cursor-pointer px-3 py-1 text-[0.75rem] text-foreground transition-colors disabled:opacity-50"
                :disabled="c.stage === 'committing' || !canCommit(c.items)"
                @click="commit(c)"
              >{{ c.stage === 'committing' ? '提交中…' : `确认保存 ${selectedOps(c.items).length} 项` }}</button>
            </div>
          </div>
        </template>

        <!-- ===== 提交成功:真实 ID/path 回读 ===== -->
        <template v-else-if="c.stage === 'done'">
          <div class="flex flex-col gap-2 border-t border-border/40 pt-3">
            <p class="text-[0.85rem]">已保存 ✓</p>
            <ul class="flex flex-col gap-0.5">
              <li v-for="(line, i) in c.summary" :key="i" class="text-[0.78rem] text-muted-foreground">{{ line }}</li>
            </ul>
            <div class="flex justify-end">
              <button class="glass-control cursor-pointer px-2.5 py-1 text-[0.75rem] text-muted-foreground hover:text-foreground" @click="remove(c)">收好了</button>
            </div>
          </div>
        </template>
      </div>
    </div>
  </Card>
</template>
