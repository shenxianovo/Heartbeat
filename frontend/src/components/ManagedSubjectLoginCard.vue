<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { submitManagedSubjectAuthorization, type ManagedSubjectStatus } from '../api/index'
import { Button } from '@/components/ui/button'
import { Card } from '@/components/ui/card'

const props = defineProps<{ subject: ManagedSubjectStatus }>()
const emit = defineEmits<{ submitted: [] }>()

const values = reactive<Record<string, string>>({})
const error = ref('')
const submitting = ref(false)
const submitted = ref(false)

watch(
  () => props.subject.authorization?.interactionId,
  () => {
    for (const key of Object.keys(values)) delete values[key]
    for (const field of props.subject.authorization?.fields ?? []) values[field.name] = ''
    error.value = ''
    submitted.value = false
  },
  { immediate: true },
)

const statusTitle = computed(() => {
  if (props.subject.authorization) return '未登录'
  if (props.subject.phase === 'Ready') return '已登录'
  if (props.subject.phase === 'Failed') return '采集器异常'
  return '正在连接'
})

const statusDescription = computed(() => {
  if (props.subject.authorization) return props.subject.authorization.message ?? '完成登录后才能采集账号状态。'
  if (props.subject.currentActivity?.title) return `当前状态：${props.subject.currentActivity.title}`
  if (props.subject.phase === 'Ready') return '暂无可展示的账号状态。'
  if (props.subject.phase === 'Failed') return '采集器未能正常运行，请检查无头 Hub 日志。'
  return '无头 Hub 正在启动这个账号的采集器。'
})

async function submit() {
  const authorization = props.subject.authorization
  if (!authorization || submitting.value || submitted.value) return
  submitting.value = true
  error.value = ''
  try {
    if (!props.subject.collectorInstanceId) throw new Error('Collector Instance is not initialized.')
    await submitManagedSubjectAuthorization(
      props.subject.collectorInstanceId,
      authorization.interactionId,
      { ...values },
    )
    submitted.value = true
    emit('submitted')
  } catch {
    error.value = '提交失败，请确认信息后重试'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <Card class="gap-4 border-border/60 bg-card/80 px-5 py-5 backdrop-blur-sm">
    <div class="flex items-start justify-between gap-4">
      <div class="min-w-0">
        <div class="truncate text-base font-semibold">{{ subject.subjectName }}</div>
        <div class="mt-1 text-xs text-muted-foreground">账号采集器</div>
      </div>
      <div class="flex shrink-0 items-center gap-2 text-sm" :class="subject.authorization ? 'text-amber-300' : 'text-muted-foreground'">
        <span class="status-dot" :class="{ alive: subject.phase === 'Ready' && !subject.authorization }"></span>
        {{ statusTitle }}
      </div>
    </div>

    <p class="text-sm leading-relaxed text-muted-foreground">{{ statusDescription }}</p>

    <form
      v-if="subject.authorization"
      class="flex flex-col gap-3 rounded-lg border border-border/50 bg-background/30 p-4"
      @submit.prevent="submit"
    >
      <div>
        <div class="text-sm font-semibold">{{ subject.authorization.title }}</div>
        <div class="mt-1 text-xs text-muted-foreground">登录信息只会发送给你的无头 Hub。</div>
      </div>

      <label
        v-for="field in subject.authorization.fields"
        :key="field.name"
        class="flex flex-col gap-1.5 text-xs text-muted-foreground"
      >
        {{ field.label }}
        <input
          v-model="values[field.name]"
          :type="field.isSecret ? 'password' : 'text'"
          :inputmode="field.inputMode ?? undefined"
          autocomplete="off"
          required
          class="glass-control px-3 py-2 text-sm text-foreground"
        />
      </label>

      <div v-if="error" class="text-xs text-red-300">{{ error }}</div>
      <div v-if="submitted" class="text-xs text-primary">已提交，等待采集器响应…</div>

      <div class="flex justify-end">
        <Button variant="glassPrimary" size="sm" type="submit" :disabled="submitting || submitted">
          {{ submitting ? '提交中…' : (subject.authorization.fields.length ? '继续' : '确认') }}
        </Button>
      </div>
    </form>
  </Card>
</template>
