import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import type { AppInfoResponse, KeyFrequencyItem } from '../api/index'
import {
  fetchAdminAppCatalog,
  fetchMe,
  fetchPublicApps,
  fetchPublicKeyFrequency,
  getTimezoneLabel,
} from '../api/index'
import { loadAdminProvisionalAppIds } from '../appCatalog/adminOverlay'
import { authStore } from '../stores/auth'
import { useAsyncData } from './useAsyncData'
import { useDeviceSelection } from './useDeviceSelection'
import { useDeviceStatus } from './useDeviceStatus'
import { useReports } from './useReports'

export function formatDuration(sec: number): string {
  const h = Math.floor(sec / 3600)
  const m = Math.floor((sec % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m`
  return '< 1m'
}

/**
 * Dashboard 的瘦协调器：持有应用元数据，组合设备选择 / 在场 / 报表三个数据域，
 * 编排 30s 报表轮询与 device/date 变更时的统一刷新。
 */
export function useHeartbeat(username: string) {
  const selection = useDeviceSelection(username)
  const { selectedDevice, selectedDate, isToday } = selection

  const appsData = useAsyncData<AppInfoResponse[]>(() => fetchPublicApps(username), [])
  const apps = appsData.data
  const provisionalAppIds = ref<Set<number>>(new Set())
  const loading = ref(false)

  const appNameMap = computed(() => {
    const map = new Map<number, string>()
    for (const app of apps.value) map.set(app.id!, app.displayName ?? app.name ?? `App ${app.id}`)
    return map
  })

  const status = useDeviceStatus(username, selection.devices, selectedDevice, isToday)
  const reports = useReports(username, selectedDevice, selectedDate)

  const kf = useAsyncData<KeyFrequencyItem[]>(() => {
    const dateObj = new Date(selectedDate.value + 'T00:00:00')
    return fetchPublicKeyFrequency(username, {
      deviceId: selectedDevice.value,
      start: dateObj.toISOString(),
      end: new Date(dateObj.getTime() + 86400000).toISOString(),
    })
  }, [])
  const keyFrequency = kf.data
  // 跨设备键频求和：打字就是打字,不存在"哪台机器的 W 键"的语义问题。
  async function loadKeyFrequency() {
    await kf.run()
  }

  async function loadAdminOverlay() {
    try {
      provisionalAppIds.value = await loadAdminProvisionalAppIds(username, {
        isAuthenticated: authStore.isAuthenticated,
        currentUsername: authStore.username.value,
        fetchMe,
        fetchInventory: fetchAdminAppCatalog,
      })
    } catch {
      // 管理员标记是附加信息；失败不能让普通 Dashboard 取数整体失败。
      provisionalAppIds.value = new Set()
    }
  }

  // 任一数据域出错就点亮:UI 据此区分"出错"与"这天没数据"。
  const error = computed(() =>
    selection.error.value
    ?? appsData.error.value
    ?? status.error.value
    ?? reports.error.value
    ?? kf.error.value,
  )

  const timezoneLabel = getTimezoneLabel()

  async function refresh() {
    loading.value = true
    try {
      // 取数不再等设备列表：默认 deviceId=0 即聚合查询。
      // 设备列表只影响选择器选项与 presence 目标,由 selection.reload() 独立拉。
      await Promise.all([
        appsData.run(),
        reports.loadUsage(),
        status.load(),
        reports.loadDaily(),
        reports.loadWeekly(),
        loadKeyFrequency(),
        loadAdminOverlay(),
      ])
    } finally {
      loading.value = false
    }
  }

  let usageTimer: ReturnType<typeof setInterval>

  onMounted(async () => {
    // 默认选中值恒为"全部设备",watch 不会因 0→N 触发,首屏必须显式加载一次。
    await refresh()

    usageTimer = setInterval(() => {
      if (isToday.value) {
        reports.loadUsage()
        reports.loadDaily()
        reports.loadWeekly()
        loadKeyFrequency()
      }
    }, 30_000)
  })

  onUnmounted(() => clearInterval(usageTimer))

  watch([selectedDevice, selectedDate], () => refresh())

  return {
    devices: selection.devices,
    error,
    refresh,
    selectedDevice,
    selectedDeviceName: selection.selectedDeviceName,
    selectedDate,
    usageData: reports.usageData,
    appNameMap,
    provisionalAppIds,
    loading,
    isToday,
    isAlive: status.isAlive,
    presences: status.presences,
    onlinePresences: status.onlinePresences,
    currentApp: status.currentApp,
    currentAppId: status.currentAppId,
    currentAppKey: status.currentAppKey,
    lastSeenStr: status.lastSeenStr,
    lastSeenTitle: status.lastSeenTitle,
    isAllDevices: selection.isAllDevices,
    appSummaries: reports.appSummaries,
    totalSeconds: reports.totalSeconds,
    usageSeconds: reports.usageSeconds,
    awaySeconds: reports.awaySeconds,
    onlineSeconds: reports.onlineSeconds,
    perDeviceSeconds: reports.perDeviceSeconds,
    hasConcurrentUse: reports.hasConcurrentUse,
    maxSeconds: reports.maxSeconds,
    activeHours: reports.activeHours,
    weeklyAppSummaries: reports.weeklyAppSummaries,
    weeklyTotalSeconds: reports.weeklyTotalSeconds,
    weeklyAwaySeconds: reports.weeklyAwaySeconds,
    includeAway: reports.includeAway,
    keyFrequency,
    timezoneLabel,
  }
}
