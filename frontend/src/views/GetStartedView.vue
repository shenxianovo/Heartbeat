<script setup lang="ts">
import { computed, ref } from 'vue'
import { authStore } from '../stores/auth'

const dashboardUrl = computed(() => authStore.username.value ? `/u/${authStore.username.value}` : '/')

const apiKeysUrl = 'https://auth.shenxianovo.com/dashboard/api-keys'

// latest 直链依赖发版资产的稳定命名（Heartbeat-win-{arch}-Setup.exe，Velopack 产物）。
const setupUrl = (arch: 'x64' | 'arm64') =>
  `https://github.com/shenxianovo/Heartbeat/releases/latest/download/Heartbeat-win-${arch}-Setup.exe`

// 默认 x64：Windows ARM 上 UA 会伪装成 x86，只有 Chromium 的 UA-CH 能给出真实架构；
// Firefox/Safari 拿不到就维持 x64（ARM 机器也能跑 x64 模拟），下方永远留另一架构的手动链接。
const arch = ref<'x64' | 'arm64'>('x64')
const otherArch = computed(() => (arch.value === 'x64' ? 'arm64' : 'x64'))

const uaData = (navigator as any).userAgentData
uaData?.getHighEntropyValues?.(['architecture'])
  .then((v: { architecture?: string }) => {
    if (v.architecture === 'arm') arch.value = 'arm64'
  })
  .catch(() => {})
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
          打开 API Key 管理页，新建一枚密钥并复制——客户端用它上传你的活动数据。
        </p>
        <a
          :href="apiKeysUrl"
          target="_blank"
          rel="noopener noreferrer"
          class="glass-control inline-flex px-3.5 py-2 text-sm font-medium text-primary no-underline"
        >打开 API Key 管理 ↗</a>
      </section>

      <section class="rounded-2xl border border-border/60 bg-card/80 p-5 backdrop-blur-sm">
        <div class="mb-4 flex h-8 w-8 items-center justify-center rounded-full bg-primary/15 font-mono text-sm font-bold text-primary">2</div>
        <h2 class="mb-2 font-semibold">下载 Windows 客户端</h2>
        <p class="mb-5 text-sm leading-relaxed text-muted-foreground">
          下载并运行安装程序。首次运行如遇 Windows 安全提示，请确认发布来源后继续。
        </p>
        <a
          :href="setupUrl(arch)"
          class="glass-control inline-flex px-3.5 py-2 text-sm font-medium text-primary no-underline"
        >下载安装包（{{ arch }}）</a>
        <p class="mt-3 text-xs text-muted-foreground/70">
          机器不是 {{ arch }}？<a
            :href="setupUrl(otherArch)"
            class="text-muted-foreground underline decoration-dotted underline-offset-2 hover:text-foreground"
          >下载 {{ otherArch }} 版本</a>
        </p>
      </section>

      <section class="rounded-2xl border border-border/60 bg-card/80 p-5 backdrop-blur-sm">
        <div class="mb-4 flex h-8 w-8 items-center justify-center rounded-full bg-primary/15 font-mono text-sm font-bold text-primary">3</div>
        <h2 class="mb-2 font-semibold">连接并开始采集</h2>
        <ol class="space-y-2 text-sm leading-relaxed text-muted-foreground">
          <li>1. 启动客户端，打开「设置」</li>
          <li>2. 粘贴刚创建的 API Key，起一个设备名称</li>
          <li>3. 保存，建议同时开启开机自启动</li>
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
