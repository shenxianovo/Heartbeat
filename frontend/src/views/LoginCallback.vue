<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { authStore } from '../stores/auth'
import { fetchMe, fetchPublicDevices } from '../api/index'

const router = useRouter()

onMounted(async () => {
  const returnTo = await authStore.handleCallback()

  // 懒建供给触发点（ADR-025）:首登必须先建 User 行,否则跳到自己看板是 404。
  // 失败不阻塞跳转——看板会再暴露问题,这里降级即可。
  if (authStore.isAuthenticated) {
    try { await fetchMe() } catch { /* 降级:跳转后由看板报错 */ }
  }

  const username = authStore.username.value
  if (username && (!returnTo || returnTo === '/')) {
    // 首次登录（尚无设备）进入安装引导；已有设备的用户保持原来的直达 Dashboard 体验。
    try {
      const devices = await fetchPublicDevices(username)
      router.replace(devices.length > 0 ? `/u/${username}` : '/get-started')
      return
    } catch { /* 降级到原有跳转 */ }
  }

  if (returnTo) {
    router.replace(returnTo)
  } else if (username) {
    router.replace(`/u/${username}`)
  } else {
    router.replace('/')
  }
})
</script>

<template>
  <div></div>
</template>
