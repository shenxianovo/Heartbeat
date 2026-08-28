<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { getAppIconObjectUrl } from '../appIcons'

const props = withDefaults(defineProps<{
  username: string
  appId: number
  alt?: string
}>(), {
  alt: '',
})

const iconUrl = ref<string | null>(null)
let loadVersion = 0

watch(
  () => ({ username: props.username, appId: props.appId }),
  async ({ username, appId }) => {
    const version = ++loadVersion
    iconUrl.value = null

    try {
      const loaded = await getAppIconObjectUrl(username, appId)
      if (version === loadVersion) iconUrl.value = loaded
    } catch {
      // 图标是装饰信息；网络或权限失败不应拖垮 Dashboard。
      if (version === loadVersion) iconUrl.value = null
    }
  },
  { immediate: true },
)

onUnmounted(() => {
  loadVersion += 1
})

function hideBrokenIcon() {
  iconUrl.value = null
}
</script>

<template>
  <img v-if="iconUrl" :src="iconUrl" :alt="alt" @error="hideBrokenIcon" />
</template>
