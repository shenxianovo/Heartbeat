<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ArrowLeft, RefreshCw } from 'lucide-vue-next'
import { fetchManagedCollectors, type ManagedCollectorStatus } from '../api/index'
import ManagedCollectorCard from '../components/ManagedCollectorCard.vue'
import { Button } from '@/components/ui/button'

const collectors = ref<ManagedCollectorStatus[]>([])
const loading = ref(true)
const refreshing = ref(false)
const error = ref('')

async function refresh(showProgress = false) {
  if (showProgress) refreshing.value = true
  try {
    collectors.value = await fetchManagedCollectors()
    error.value = ''
  } catch {
    error.value = '无法连接 Hub 或 Collector Catalog，请确认部署状态'
  } finally {
    loading.value = false
    refreshing.value = false
  }
}

onMounted(() => void refresh())
</script>

<template>
  <div class="mx-auto w-[min(100%,800px)] px-4 py-8 sm:px-8">
    <header class="mb-8 flex flex-wrap items-start justify-between gap-4">
      <div>
        <h1 class="text-2xl font-bold">Hub 管理</h1>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          从 Collector Catalog 安装采集器、查看运行状态，并完成采集器请求的授权。
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
    <p v-if="loading" class="text-sm text-muted-foreground">加载 Collector Catalog…</p>
    <div v-else-if="collectors.length" class="flex flex-col gap-4">
      <ManagedCollectorCard
        v-for="collector in collectors"
        :key="collector.packageId"
        :collector="collector"
        @changed="refresh()"
      />
    </div>
    <div v-else-if="!error" class="rounded-lg border border-border/60 bg-card/60 px-5 py-8 text-center text-sm text-muted-foreground">
      Catalog 暂无适用于这个 Hub 的 Collector。
    </div>
  </div>
</template>
