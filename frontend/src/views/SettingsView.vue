<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ArrowLeft } from 'lucide-vue-next'
import { authStore } from '../stores/auth'
import { fetchMe, updateMySettings } from '../api/index'
import { Button } from '@/components/ui/button'

const loading = ref(true)
const saving = ref(false)
const error = ref('')
const username = ref('')
const isPublic = ref(false)
const isAdmin = ref(false)
const catalogDenied = new URLSearchParams(window.location.search).get('catalogDenied') === '1'

onMounted(async () => {
  try {
    const me = await fetchMe()
    username.value = me.username
    isPublic.value = me.isPublic
    isAdmin.value = me.isAdmin
  } catch {
    error.value = '加载设置失败，请刷新重试'
  } finally {
    loading.value = false
  }
})

async function toggleVisibility() {
  if (saving.value) return
  const next = !isPublic.value
  saving.value = true
  error.value = ''
  try {
    const me = await updateMySettings(next)
    isPublic.value = me.isPublic
  } catch {
    error.value = '保存失败，请重试'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="settings">
    <header class="settings-header">
      <h1>设置</h1>
      <div class="header-actions">
        <Button v-if="username" variant="glass" size="sm" as-child>
          <router-link :to="`/u/${username}`">
            <ArrowLeft />
            返回看板
          </router-link>
        </Button>
        <Button variant="glass" size="sm" @click="authStore.logout()">登出</Button>
      </div>
    </header>

    <p v-if="loading" class="placeholder">加载中…</p>

    <template v-else>
      <div v-if="error" class="error">{{ error }}</div>
      <div v-if="catalogDenied" class="error">只有部署管理员可以访问 App Catalog。</div>

      <section class="row">
        <div class="row-text">
          <div class="row-title">知识管理</div>
          <div class="row-desc">
            查看和修正 Strand 树、指纹、片段事实与复现探针。
          </div>
        </div>
        <Button variant="glassPrimary" size="sm" as-child>
          <router-link to="/settings/knowledge">管理</router-link>
        </Button>
      </section>

      <section v-if="isAdmin" class="row">
        <div class="row-text">
          <div class="row-title">App Catalog</div>
          <div class="row-desc">
            归类待识别的跨平台应用、检查本地 Override，并导出候选 Catalog JSON。
          </div>
        </div>
        <Button variant="glassPrimary" size="sm" as-child>
          <router-link to="/settings/app-catalog">管理</router-link>
        </Button>
      </section>

      <section class="row">
        <div class="row-text">
          <div class="row-title">公开看板</div>
          <div class="row-desc">
            开启后，任何人可通过
            <code>/u/{{ username }}</code>
            访问你的看板（含窗口标题、设备在线状态等全部数据）。关闭时仅你本人可见。
          </div>
        </div>
        <button
          class="switch"
          :class="{ on: isPublic }"
          :disabled="saving"
          role="switch"
          :aria-checked="isPublic"
          @click="toggleVisibility"
        >
          <span class="knob"></span>
        </button>
      </section>
    </template>
  </div>
</template>

<style scoped>
.settings {
  width: min(100%, 800px);
  margin: 0 auto;
  padding: 2rem;
}
.settings-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 2rem;
}
.header-actions { display: flex; align-items: center; gap: 0.5rem; }
h1 { font-size: 1.5rem; font-weight: 700; }
.placeholder { color: var(--muted-foreground); }
.error {
  margin-bottom: 1rem;
  padding: 0.6rem 0.9rem;
  border: 1px solid rgb(239 68 68 / 0.3);
  background: rgb(239 68 68 / 0.1);
  border-radius: 8px;
  color: rgb(252 165 165);
  font-size: 0.85rem;
}
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1.5rem;
  padding: 1rem 0;
  border-top: 1px solid var(--border);
}
.row-title { font-weight: 600; margin-bottom: 0.3rem; }
.row-desc { color: var(--muted-foreground); font-size: 0.85rem; line-height: 1.5; }
.row-desc code {
  padding: 0.05rem 0.3rem;
  background: var(--card);
  border-radius: 4px;
  font-size: 0.8rem;
}
.switch {
  flex-shrink: 0;
  width: 44px;
  height: 24px;
  border-radius: 999px;
  border: none;
  background: var(--border);
  cursor: pointer;
  padding: 2px;
  transition: background 0.15s;
}
.switch.on { background: var(--primary); }
.switch:disabled { opacity: 0.6; cursor: default; }
.knob {
  display: block;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  background: #fff;
  transition: transform 0.15s;
}
.switch.on .knob { transform: translateX(20px); }
</style>
