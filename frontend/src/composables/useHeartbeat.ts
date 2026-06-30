import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import type { AppInfoResponse } from '../api/index'
import { fetchPublicApps, fetchPublicKeyFrequency, getTimezoneLabel } from '../api/index'
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

  const apps = ref<AppInfoResponse[]>([])
  const loading = ref(false)

  const appNameMap = computed(() => {
    const map = new Map<number, string>()
    for (const app of apps.value) map.set(app.id!, app.name!)
    return map
  })

  const status = useDeviceStatus(username, selectedDevice, isToday, appNameMap)
  const reports = useReports(username, selectedDevice, selectedDate)

  const keyFrequency = ref<{ code: number; count: number }[]>([])
  async function loadKeyFrequency() {
    if (!selectedDevice.value) return
    const dateObj = new Date(selectedDate.value + 'T00:00:00')
    const start = dateObj.toISOString()
    const end = new Date(dateObj.getTime() + 86400000).toISOString()
    const res = await fetchPublicKeyFrequency(username, { deviceId: selectedDevice.value, start, end })
    keyFrequency.value = res.keys
  }

  const timezoneLabel = getTimezoneLabel()

  async function refresh() {
    loading.value = true
    try {
      await Promise.all([
        reports.loadUsage(),
        status.load(),
        reports.loadDaily(),
        reports.loadWeekly(),
        loadKeyFrequency(),
      ])
    } finally {
      loading.value = false
    }
  }

  let usageTimer: ReturnType<typeof setInterval>

  onMounted(async () => {
    apps.value = await fetchPublicApps(username)

    usageTimer = setInterval(() => {
      if (isToday.value) {
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
    selectedDevice,
    selectedDeviceName: selection.selectedDeviceName,
    selectedDate,
    usageData: reports.usageData,
    appNameMap,
    loading,
    isToday,
    isAlive: status.isAlive,
    currentApp: status.currentApp,
    currentAppId: status.currentAppId,
    lastSeenStr: status.lastSeenStr,
    appSummaries: reports.appSummaries,
    totalSeconds: reports.totalSeconds,
    usageSeconds: reports.usageSeconds,
    awaySeconds: reports.awaySeconds,
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
