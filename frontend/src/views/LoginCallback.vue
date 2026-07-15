<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { authStore } from '../stores/auth'
import { fetchMe } from '../api/index'

const router = useRouter()

onMounted(async () => {
  const returnTo = await authStore.handleCallback()

  // 懒建供给触发点（ADR-025）:首登必须先建 User 行,否则跳到自己看板是 404。
  // 失败不阻塞跳转——看板会再暴露问题,这里降级即可。
  if (authStore.isAuthenticated) {
    try { await fetchMe() } catch { /* 降级:跳转后由看板报错 */ }
  }

  if (returnTo) {
    router.replace(returnTo)
  } else if (authStore.username.value) {
    router.replace(`/u/${authStore.username.value}`)
  } else {
    router.replace('/')
  }
})
</script>

<template>
  <div></div>
</template>
