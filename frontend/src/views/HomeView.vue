<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { authStore } from '../stores/auth'

const router = useRouter()

// 已登录直接去自己的看板；landing 只给未登录访客看。
onMounted(() => {
  if (authStore.isAuthenticated && authStore.username.value) {
    router.replace(`/u/${authStore.username.value}`)
  }
})
</script>

<template>
  <div v-if="!authStore.isAuthenticated" class="relative z-10 flex min-h-screen flex-col items-center justify-center px-6 py-16 text-center">
    <div class="flex max-w-2xl flex-col items-center gap-6">
      <h1 class="flex items-center gap-3 font-display text-[clamp(2rem,6vw,3.5rem)] font-bold tracking-tight">
        <span class="status-dot alive"></span>
        Heartbeat
      </h1>
      <p class="max-w-xl text-balance text-[clamp(1rem,2.5vw,1.15rem)] leading-relaxed text-muted-foreground">
        记录你在电脑前的每一次心跳——应用时长、活动回放、键盘热力图，以及属于每一天的 Recap。
      </p>
      <p class="max-w-lg text-balance text-sm leading-relaxed text-muted-foreground/80">
        登录后，下载 Windows 客户端并使用 Auth 平台的 API Key 连接设备，数据就会出现在你的个人 Dashboard。
      </p>
      <div class="flex flex-wrap items-center justify-center gap-3">
        <button
          class="glass-control cursor-pointer px-6 py-2.5 text-[0.95rem] font-medium text-primary"
          @click="authStore.redirectToLogin()"
        >登录并开始使用</button>
        <RouterLink
          to="/u/shenxianovo"
          class="glass-control px-6 py-2.5 text-[0.95rem] text-muted-foreground no-underline transition-colors hover:text-foreground"
        >查看示例 Dashboard</RouterLink>
      </div>
      <div class="mt-2 flex flex-wrap items-center justify-center gap-x-5 gap-y-2 text-xs text-muted-foreground/70">
        <span>Windows 客户端</span>
        <span>数据归你所有</span>
        <span>可选择公开 Dashboard</span>
      </div>
    </div>
  </div>
</template>
