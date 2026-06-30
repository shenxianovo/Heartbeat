import { ref, computed, onMounted } from 'vue'
import type { DeviceInfoResponse } from '../api/index'
import { fetchPublicDevices, fetchPublicDeviceStatus } from '../api/index'

function todayStr(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/**
 * 设备选择枢纽：当前看哪台设备、哪一天。其余数据域都以此为输入。
 * onMounted 时拉取设备列表并自动选中第一台在线设备。
 */
export function useDeviceSelection(username: string) {
  const devices = ref<DeviceInfoResponse[]>([])
  const selectedDevice = ref(0)
  const selectedDate = ref(todayStr())

  const selectedDeviceName = computed(() => {
    const d = devices.value.find(d => d.id === selectedDevice.value)
    return d?.name ?? ''
  })

  const isToday = computed(() => selectedDate.value === todayStr())

  onMounted(async () => {
    devices.value = await fetchPublicDevices(username)

    if (devices.value.length > 0) {
      let picked = devices.value[0].id!
      for (const d of devices.value) {
        const s = await fetchPublicDeviceStatus(username, d.id!)
        if (s?.isOnline) { picked = d.id!; break }
      }
      selectedDevice.value = picked
    }
  })

  return {
    devices,
    selectedDevice,
    selectedDate,
    selectedDeviceName,
    isToday,
  }
}
