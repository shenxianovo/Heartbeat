<script setup lang="ts">
import { StrandRefDto, type IKnowledgeProposalResponse, type IStrandResponse } from '../api/index'
import {
  groupByCategory, precheck, strandRefOptions, strandDisplay, episodeRefDisplay,
  rebindMatcherTarget, refToValue, valueToRef, dateToInput, inputToDate,
  describeMatcher, formatTimeRange, OP_META, type ReviewItem,
} from '../teaching/teachingFlow'

/**
 * KnowledgeChangeSet 提案审阅（ADR-031 §6，issue 05/06 共用）：分区渲染 + 逐项启用/内联
 * 编辑。纯展示与编辑，不做取数、不提交——提交由各入口的编排负责（主动教学 / Recap 纠正）。
 * 可判定规则全部在 teachingFlow，这里保持薄。
 */
const props = defineProps<{
  proposal: IKnowledgeProposalResponse | null
  items: ReviewItem[]
  strands: IStrandResponse[]
  readingLabels: Record<string, string>
  /** 提交进行中：禁编辑。 */
  locked: boolean
  /** 上次提交失败定位到的操作：标红。 */
  failedOpId: string | null
}>()

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
  if (parsed) rebindMatcherTarget(item.op, parsed, props.strands)
}

const inputCls = 'w-full rounded-md border border-border/50 bg-background/60 px-2.5 py-1.5 text-[0.85rem] outline-none focus:border-border'
const selectCls = 'w-full rounded-md border border-border/50 bg-background/60 px-2 py-1.5 text-[0.85rem] outline-none focus:border-border'
const labelCls = 'text-[0.72rem] text-muted-foreground/80'
</script>

<template>
  <div class="flex flex-col gap-3">
    <p v-if="proposal?.explanation" class="text-[0.85rem] text-muted-foreground">{{ proposal.explanation }}</p>

    <ul v-if="(proposal?.warnings ?? []).length > 0" class="flex flex-col gap-0.5">
      <li v-for="(w, i) in proposal!.warnings" :key="i" class="text-[0.75rem] text-amber-600 dark:text-amber-500">⚠ {{ w }}</li>
    </ul>
    <ul v-if="(proposal?.suggestions ?? []).length > 0" class="flex flex-col gap-0.5">
      <li v-for="(s, i) in proposal!.suggestions" :key="i" class="text-[0.75rem] text-muted-foreground/70">💡 {{ s }}（无需保存）</li>
    </ul>

    <!-- 分区渲染:脉络 / 指纹 / 片段事实 / 探针 -->
    <div v-for="group in groupByCategory(items)" :key="group.category" class="flex flex-col gap-2">
      <h3 class="text-[0.72rem] font-semibold uppercase tracking-[0.06em] text-muted-foreground/70">{{ group.label }}</h3>

      <div
        v-for="item in group.items"
        :key="item.op.opId ?? ''"
        class="flex flex-col gap-2 rounded-md border px-3 py-2.5 transition-colors"
        :class="[
          failedOpId && failedOpId === item.op.opId ? 'border-destructive' : 'border-border/40',
          item.enabled ? 'bg-background/50' : 'bg-background/20 opacity-60',
        ]"
      >
        <div class="flex items-center gap-2">
          <label class="flex cursor-pointer items-center gap-2 text-[0.82rem] font-medium">
            <input v-model="item.enabled" type="checkbox" class="accent-foreground" :disabled="locked" />
            {{ OP_META[item.op.type ?? '']?.label ?? item.op.type }}
          </label>
          <span v-if="failedOpId && failedOpId === item.op.opId" class="text-[0.72rem] text-destructive">← 上次提交在这里失败</span>
        </div>

        <template v-if="item.enabled">
          <!-- createStrand -->
          <div v-if="item.op.type === 'createStrand' && item.op.createStrand" class="flex flex-col gap-1.5">
            <input v-model="item.op.createStrand.name" type="text" placeholder="名字" :class="inputCls" />
            <input v-model="item.op.createStrand.gloss" type="text" placeholder="一句话说明(常一起开的通用工具也写在这里)" :class="inputCls" />
            <div class="flex items-center gap-2">
              <span :class="labelCls">父级</span>
              <select :value="refToValue(item.op.createStrand.parent)" :class="selectCls" @change="setStrandRef(item.op.createStrand, 'parent', $event)">
                <option v-for="opt in strandRefOptions(items, items.indexOf(item), strands, '顶层(无父级)')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
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
                <option v-for="opt in strandRefOptions(items, items.indexOf(item), strands, '顶层(无父级)')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
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
                <option v-for="opt in strandRefOptions(items, items.indexOf(item), strands, null)" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
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
                <option v-for="opt in strandRefOptions(items, items.indexOf(item), strands, '不关联(独立存在)')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
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
            <p :class="labelCls">片段事实：{{ episodeRefDisplay(item.op.relateEpisode.episode, items) }}</p>
            <div class="flex items-center gap-2">
              <span :class="labelCls">关联到</span>
              <select :value="refToValue(item.op.relateEpisode.relatedStrand)" :class="selectCls" @change="setStrandRef(item.op.relateEpisode, 'relatedStrand', $event)">
                <option v-for="opt in strandRefOptions(items, items.indexOf(item), strands, '解除关联')" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
              </select>
            </div>
          </div>

          <!-- createProbe -->
          <div v-else-if="item.op.type === 'createProbe' && item.op.createProbe" class="flex flex-col gap-1">
            <p :class="labelCls">在「{{ episodeRefDisplay(item.op.createProbe.episode, items) }}」上留一个探针</p>
            <p :class="labelCls">谓词：<span class="font-mono">{{ describeMatcher(item.op.createProbe.matcher, readingLabels) }}</span>——再次出现时会来问你是否提升为持续脉络,不会自动写入任何归属。</p>
          </div>

          <!-- resolveProbe -->
          <div v-else-if="item.op.type === 'resolveProbe' && item.op.resolveProbe" class="flex flex-col gap-1">
            <p :class="labelCls">{{ item.op.resolveProbe.resolution === 'muted' ? '静音这个探针(不再提醒)' : '否认复现(这不是同一件事)' }}——之后不再就它发问。</p>
          </div>

          <!-- promoteEpisode -->
          <div v-else-if="item.op.type === 'promoteEpisode' && item.op.promoteEpisode" class="flex flex-col gap-1.5">
            <p :class="labelCls">提升：{{ episodeRefDisplay(item.op.promoteEpisode.episode, items) }}</p>
            <div class="flex items-center gap-2">
              <span :class="labelCls">到脉络</span>
              <select :value="refToValue(item.op.promoteEpisode.strand)" :class="selectCls" @change="setStrandRef(item.op.promoteEpisode, 'strand', $event)">
                <option v-for="opt in strandRefOptions(items, items.indexOf(item), strands, null)" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
              </select>
            </div>
            <p :class="labelCls">非破坏性：原片段事实会保留并关联到该脉络,不是把它转换成脉络。</p>
          </div>
        </template>
      </div>
    </div>

    <!-- 提交前预检警告(服务端可预知的约束;最终仍以服务端校验为准) -->
    <ul v-if="precheck(items).warnings.length > 0" class="flex flex-col gap-0.5">
      <li v-for="(w, i) in precheck(items).warnings" :key="i" class="text-[0.75rem] text-amber-600 dark:text-amber-500">⚠ {{ w }}</li>
    </ul>
  </div>
</template>
