<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { authStore } from '../stores/auth'

const router = useRouter()

// 已登录直接去自己的看板;landing 只给未登录访客看
onMounted(() => {
  if (authStore.isAuthenticated && authStore.username.value) {
    router.replace(`/u/${authStore.username.value}`)
  }
})
</script>

<template>
  <div v-if="!authStore.isAuthenticated" class="relative z-10 flex min-h-screen flex-col items-center justify-center gap-6 px-6 text-center">
    <h1 class="flex items-center gap-3 font-display text-[clamp(2rem,6vw,3.5rem)] font-bold tracking-tight">
      <span class="status-dot alive"></span>
      Heartbeat
    </h1>
    <p class="max-w-md text-balance text-[clamp(0.95rem,2.5vw,1.1rem)] leading-relaxed text-muted-foreground">
      记录你在电脑前的每一次心跳——应用时长、活动回放、键盘热力图。
    </p>
    <button
      class="glass-control cursor-pointer px-6 py-2.5 text-[0.95rem] font-medium text-primary"
      @click="authStore.redirectToLogin()"
    >登录</button>
  </div>
</template>
