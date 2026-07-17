<script setup lang="ts">
import { computed, watch } from 'vue'
import { fetchDailyRecap, fetchPublicDailyRecap, type DailyRecapResponse } from '../api/index'
import { useAsyncData } from '../composables/useAsyncData'
import { Card } from '@/components/ui/card'

/**
 * 当日 Recap 卡片（ADR-023）：owner 可生成/重新生成；访客只能读取 owner 已生成的缓存。
 * 公开读取永不触发 LLM，既避免访客烧 token，也让“重新生成”保持 owner-only。
 */
const props = defineProps<{
  selectedDate: string
  username: string
  canRegenerate: boolean
}>()

let forceNext = false
const recap = useAsyncData<DailyRecapResponse | null>(
  () => props.canRegenerate
    ? fetchDailyRecap({ date: props.selectedDate, force: forceNext })
    : fetchPublicDailyRecap(props.username, { date: props.selectedDate }),
  null,
)

async function load(force = false) {
  forceNext = force
  try {
    await recap.run()
  } finally {
    forceNext = false
  }
}

watch(() => props.selectedDate, () => {
  recap.data.value = null // 切日期不展示上一天的旧叙事
  load()
}, { immediate: true })

const paragraphs = computed(() =>
  (recap.data.value?.narrative ?? '').split(/\n+/).map(s => s.trim()).filter(Boolean)
)

const generatedAtStr = computed(() => {
  const at = recap.data.value?.generatedAt
  if (!at) return ''
  return new Date(at).toLocaleString('zh-CN', {
    month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit',
  })
})

const isUnavailableToVisitor = computed(() =>
  !props.canRegenerate && recap.error.value?.kind === 'http' && recap.error.value.status === 404
)

const errorMessage = computed(() => {
  const e = recap.error.value
  if (!e) return ''
  if (e.kind === 'network') return '网络连接失败，请检查网络后重试'
  if (e.kind === 'http' && e.status === 502) return '生成服务暂不可用，请稍后重试'
  if (e.kind === 'http') return `服务器返回错误（${e.status}），请稍后重试`
  return '数据解析失败，请重试'
})
</script>

<template>
  <Card v-if="!isUnavailableToVisitor" class="mb-6 gap-3 border-border/60 bg-card/80 py-5 backdrop-blur-sm">
    <div class="flex flex-col gap-3 px-5">
      <div class="flex items-center justify-between gap-3">
        <h2 class="text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground">这一天 · Recap</h2>
        <button
          v-if="canRegenerate && recap.data.value && !recap.data.value.isEmpty"
          class="glass-control cursor-pointer whitespace-nowrap px-2.5 py-1 text-[0.75rem] text-muted-foreground transition-colors hover:text-foreground disabled:cursor-default disabled:opacity-50"
          :disabled="recap.pending.value"
          title="用最新数据重新生成这一天的回顾"
          @click="load(true)"
        >重新生成</button>
      </div>

      <!-- 首次生成中（无旧数据可展示时） -->
      <div v-if="recap.pending.value && !recap.data.value" class="py-6 text-center text-[0.9rem] text-muted-foreground">
        <span class="recap-thinking">正在回忆这一天…</span>
      </div>

      <!-- 出错（保留上次成功的叙事时不打断阅读，只在无数据时占位） -->
      <div
        v-else-if="errorMessage && !recap.data.value"
        class="flex items-center justify-between gap-3 py-2 text-[0.85rem] text-muted-foreground"
      >
        <span>{{ errorMessage }}</span>
        <button
          class="glass-control shrink-0 cursor-pointer px-2.5 py-1 text-[0.75rem]"
          :disabled="recap.pending.value"
          @click="load()"
        >重试</button>
      </div>

      <!-- 空日 -->
      <div v-else-if="recap.data.value?.isEmpty" class="py-6 text-center text-[0.9rem] text-muted-foreground">
        这一天没有记录。
      </div>

      <!-- 叙事 -->
      <template v-else-if="recap.data.value">
        <div class="flex flex-col gap-2.5 text-[0.92rem] leading-relaxed text-foreground/90">
          <p v-for="(p, i) in paragraphs" :key="i">{{ p }}</p>
        </div>
        <div class="flex items-center justify-between gap-3 text-[0.72rem] text-muted-foreground/80">
          <span>生成于 {{ generatedAtStr }}<template v-if="recap.data.value.model"> · {{ recap.data.value.model }}</template></span>
          <span v-if="recap.pending.value" class="recap-thinking">更新中…</span>
          <span v-else-if="errorMessage">{{ errorMessage }}</span>
        </div>
      </template>
    </div>
  </Card>
</template>

<style scoped>
.recap-thinking {
  animation: recap-pulse 1.6s ease-in-out infinite;
}
@keyframes recap-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}
</style>
