import { ref, computed, onMounted } from 'vue'
import type { DeviceInfoResponse } from '../api/index'
import { fetchPublicDevices } from '../api/index'
import { useAsyncData } from './useAsyncData'

function todayStr(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** "全部设备"哨兵：deviceId=0 表示跨设备聚合，在 API 边界归一为不传 deviceId。 */
export const ALL_DEVICES = 0

/**
 * 设备选择枢纽：当前看哪台设备（0 = 全部设备）、哪一天。其余数据域都以此为输入。
 *
 * 默认恒为"全部设备"：不做在线探测、不按当天有无 usage 回退单设备。
 * 选择器状态因此与数据无关——切日期不会让控件自己跳值，取数也不再等设备列表就位
 * （设备列表只是选择器的选项来源）。单台活跃时聚合视图自然退化成单设备看板。
 */
export function useDeviceSelection(username: string) {
  const devicesData = useAsyncData<DeviceInfoResponse[]>(() => fetchPublicDevices(username), [])
  const devices = devicesData.data
  const selectedDevice = ref<number>(ALL_DEVICES)
  const selectedDate = ref(todayStr())

  const isAllDevices = computed(() => selectedDevice.value === ALL_DEVICES)

  const selectedDeviceName = computed(() => {
    if (isAllDevices.value) return '全部设备'
    const d = devices.value.find(d => d.id === selectedDevice.value)
    return d?.name ?? ''
  })

  const isToday = computed(() => selectedDate.value === todayStr())

  /** 拉设备列表（选择器选项）。挂载时跑一次,错误重试时可重跑。不改变选中值。 */
  async function reload() {
    await devicesData.run()
  }

  onMounted(reload)

  return {
    devices,
    error: devicesData.error,
    reload,
    selectedDevice,
    selectedDate,
    selectedDeviceName,
    isAllDevices,
    isToday,
  }
}
