<script setup lang="ts">
import { computed } from 'vue'
import { authStore } from '../stores/auth'

const dashboardUrl = computed(() => authStore.username.value ? `/u/${authStore.username.value}` : '/')

const releaseUrl = 'https://github.com/shenxianovo/Heartbeat/releases/latest'
const authUrl = 'https://auth.shenxianovo.com'
</script>

<template>
  <div class="relative z-10 mx-auto min-h-screen w-[min(100%,960px)] px-[clamp(1rem,4vw,3rem)] py-[clamp(2rem,6vw,5rem)]">
    <header class="mb-8 flex flex-wrap items-center justify-between gap-4 pr-12">
      <div>
        <p class="mb-2 text-xs font-semibold uppercase tracking-[0.12em] text-primary">开始使用</p>
        <h1 class="font-display text-[clamp(1.8rem,5vw,3rem)] font-bold tracking-tight">让第一条心跳抵达 Dashboard</h1>
      </div>
      <RouterLink
        :to="dashboardUrl"
        class="glass-control px-4 py-2 text-sm text-muted-foreground no-underline hover:text-foreground"
      >进入 Dashboard</RouterLink>
    </header>

    <p class="mb-8 max-w-2xl text-balance leading-relaxed text-muted-foreground">
      Heartbeat 需要 Windows 客户端采集活动，并使用你在 Auth 平台创建的 API Key 安全上传。通常三步就能完成。
    </p>

    <div class="grid gap-4 md:grid-cols-3">
      <section class="rounded-2xl border border-border/60 bg-card/80 p-5 backdrop-blur-sm">
        <div class="mb-4 flex h-8 w-8 items-center justify-center rounded-full bg-primary/15 font-mono text-sm font-bold text-primary">1</div>
        <h2 class="mb-2 font-semibold">创建 API Key</h2>
        <p class="mb-5 text-sm leading-relaxed text-muted-foreground">
          前往 Auth 平台登录，在 API Key 管理中创建一枚供 Heartbeat 客户端使用的密钥。
        </p>
        <a
          :href="authUrl"
          target="_blank"
          rel="noopener noreferrer"
          class="glass-control inline-flex px-3.5 py-2 text-sm font-medium text-primary no-underline"
        >打开 Auth 平台 ↗</a>
      </section>

      <section class="rounded-2xl border border-border/60 bg-card/80 p-5 backdrop-blur-sm">
        <div class="mb-4 flex h-8 w-8 items-center justify-center rounded-full bg-primary/15 font-mono text-sm font-bold text-primary">2</div>
        <h2 class="mb-2 font-semibold">下载 Windows 客户端</h2>
        <p class="mb-5 text-sm leading-relaxed text-muted-foreground">
          从 GitHub Releases 下载最新版本。首次运行如遇 Windows 安全提示，请确认发布来源后继续。
        </p>
        <a
          :href="releaseUrl"
          target="_blank"
          rel="noopener noreferrer"
          class="glass-control inline-flex px-3.5 py-2 text-sm font-medium text-primary no-underline"
        >下载最新版本 ↗</a>
      </section>

      <section class="rounded-2xl border border-border/60 bg-card/80 p-5 backdrop-blur-sm">
        <div class="mb-4 flex h-8 w-8 items-center justify-center rounded-full bg-primary/15 font-mono text-sm font-bold text-primary">3</div>
        <h2 class="mb-2 font-semibold">连接并开始采集</h2>
        <ol class="space-y-2 text-sm leading-relaxed text-muted-foreground">
          <li>1. 打开客户端的「设置」</li>
          <li>2. 粘贴 API Key，填写设备名称</li>
          <li>3. 保存配置，并按需开启开机自启动</li>
        </ol>
      </section>
    </div>

    <div class="mt-6 flex flex-wrap items-center justify-between gap-4 rounded-2xl border border-primary/20 bg-primary/5 px-5 py-4">
      <p class="text-sm leading-relaxed text-muted-foreground">
        保存后等待约一分钟，再回到 Dashboard。如果还没有数据，可检查客户端「运行日志」。
      </p>
      <RouterLink
        :to="dashboardUrl"
        class="glass-control shrink-0 px-4 py-2 text-sm font-medium text-primary no-underline"
      >我已完成，查看数据</RouterLink>
    </div>
  </div>
</template>
