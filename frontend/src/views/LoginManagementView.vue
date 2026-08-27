<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { ArrowLeft, RefreshCw } from 'lucide-vue-next'
import { fetchManagedSubjectStatuses, type ManagedSubjectStatus } from '../api/index'
import ManagedSubjectLoginCard from '../components/ManagedSubjectLoginCard.vue'
import { Button } from '@/components/ui/button'

const subjects = ref<ManagedSubjectStatus[]>([])
const loading = ref(true)
const refreshing = ref(false)
const error = ref('')
let poll: ReturnType<typeof setInterval> | null = null

async function refresh(showProgress = false) {
  if (showProgress) refreshing.value = true
  try {
    subjects.value = await fetchManagedSubjectStatuses()
    error.value = ''
  } catch {
    error.value = '无法连接无头 Hub，请确认本地栈正在运行'
  } finally {
    loading.value = false
    refreshing.value = false
  }
}

onMounted(() => {
  void refresh()
  poll = setInterval(() => void refresh(), 5_000)
})

onUnmounted(() => {
  if (poll) clearInterval(poll)
})
</script>

<template>
  <div class="mx-auto w-[min(100%,800px)] px-4 py-8 sm:px-8">
    <header class="mb-8 flex flex-wrap items-start justify-between gap-4">
      <div>
        <h1 class="text-2xl font-bold">登录管理</h1>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          管理由无头 Hub 托管的账号登录。账号状态仍会照常显示在看板的“当前使用”。
        </p>
      </div>
      <div class="flex gap-2">
        <Button variant="glass" size="sm" :disabled="refreshing" @click="refresh(true)">
          <RefreshCw />
          刷新
        </Button>
        <Button variant="glass" size="sm" as-child>
          <router-link to="/settings">
            <ArrowLeft />
            返回设置
          </router-link>
        </Button>
      </div>
    </header>

    <div v-if="error" class="mb-4 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-200">
      {{ error }}
    </div>

    <p v-if="loading" class="text-sm text-muted-foreground">加载账号…</p>
    <div v-else-if="subjects.length" class="flex flex-col gap-4">
      <ManagedSubjectLoginCard
        v-for="subject in subjects"
        :key="subject.subjectId"
        :subject="subject"
        @submitted="refresh()"
      />
    </div>
    <div v-else-if="!error" class="rounded-lg border border-border/60 bg-card/60 px-5 py-8 text-center text-sm text-muted-foreground">
      尚未配置需要登录的账号采集器。
    </div>
  </div>
</template>
