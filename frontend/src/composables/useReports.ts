import { ref, computed, type Ref } from 'vue'
import type { AppSummary, AppUsageResponse, DailyReportResponse, WeeklyReportResponse } from '../api/index'
import { fetchPublicUsage, fetchPublicDailyReport, fetchPublicWeeklyReport } from '../api/index'
import { AWAY_APP } from '../appLabels'
import { formatTitle } from '../titleFormatters'

interface AppDurationLike {
  appId?: number
  appName?: string
  durationSeconds?: number
}

/** away 政策收口处：从报表明细里分离真实应用与"离开"时间。 */
function realApps(apps: AppDurationLike[] | undefined): AppSummary[] {
  if (!apps) return []
  return apps
    .filter(a => a.appName !== AWAY_APP)
    .map(a => ({
      appId: a.appId!,
      appName: a.appName ?? `App ${a.appId}`,
      totalSeconds: a.durationSeconds!,
    }))
    .sort((a, b) => b.totalSeconds - a.totalSeconds)
}

function awayOf(apps: AppDurationLike[] | undefined): number {
  return apps?.find(a => a.appName === AWAY_APP)?.durationSeconds ?? 0
}

/**
 * 统计/回放域：日报、周报、原始用量段，以及由它们派生的排行、求和、活跃小时。
 * away 政策（includeAway 开关 + 过滤/求和）集中在此处，不散落到组件。
 */
export function useReports(
  username: string,
  selectedDevice: Ref<number>,
  selectedDate: Ref<string>,
) {
  const usageData = ref<AppUsageResponse[]>([])
  const dailyReport = ref<DailyReportResponse | null>(null)
  const weeklyReport = ref<WeeklyReportResponse | null>(null)

  // 是否把"离开"时间（息屏/睡眠/锁屏）计入统计。默认不计入。详见 ADR-014。
  const includeAway = ref(false)

  // ── 日报 ──
  const appSummaries = computed(() => realApps(dailyReport.value?.apps))
  const awaySeconds = computed(() => awayOf(dailyReport.value?.apps))
  const usageSeconds = computed(() => appSummaries.value.reduce((s, a) => s + a.totalSeconds, 0))
  const totalSeconds = computed(() =>
    usageSeconds.value + (includeAway.value ? awaySeconds.value : 0)
  )
  const maxSeconds = computed(() => appSummaries.value[0]?.totalSeconds ?? 1)

  // ── 周报 ──
  const weeklyAppSummaries = computed(() => realApps(weeklyReport.value?.apps))
  const weeklyAwaySeconds = computed(() => awayOf(weeklyReport.value?.apps))
  const weeklyUsageSeconds = computed(() => weeklyAppSummaries.value.reduce((s, a) => s + a.totalSeconds, 0))
  const weeklyTotalSeconds = computed(() =>
    weeklyUsageSeconds.value + (includeAway.value ? weeklyAwaySeconds.value : 0)
  )

  // ── 活跃小时（时间线热力图用），away 不算活跃 ──
  const activeHours = computed(() => {
    const hours = new Set<number>()
    for (const u of usageData.value) {
      if (u.appName === AWAY_APP) continue
      const s = u.startTime!.getHours()
      const e = u.endTime!.getHours()
      if (e >= s) {
        for (let h = s; h <= e; h++) hours.add(h)
      } else {
        for (let h = s; h < 24; h++) hours.add(h)
      }
    }
    return hours
  })

  async function loadUsage() {
    if (!selectedDevice.value) return
    const dateObj = new Date(selectedDate.value + 'T00:00:00')
    const start = dateObj.toISOString()
    const end = new Date(dateObj.getTime() + 86400000).toISOString()
    usageData.value = await fetchPublicUsage(username, { deviceId: selectedDevice.value, start, end })
  }

  async function loadDaily() {
    if (!selectedDevice.value) return
    dailyReport.value = await fetchPublicDailyReport(username, { deviceId: selectedDevice.value, date: selectedDate.value })
  }

  async function loadWeekly() {
    if (!selectedDevice.value) return
    weeklyReport.value = await fetchPublicWeeklyReport(username, { deviceId: selectedDevice.value, date: selectedDate.value })
  }

  /**
   * 某个 App 在当前 usageData 内、按格式化后标题聚合的时长明细（降序）。
   * 标题先过 formatTitle 归一化（无损，仅展示），故 spinner 变体等会自动合并计数。
   * 详见 ADR-015 / ADR-016。
   */
  function titleBreakdown(appId: number): { title: string; secondary?: string; category?: string; totalSeconds: number; count: number }[] {
    const byTitle = new Map<string, { secondary?: string; category?: string; totalSeconds: number; count: number }>()
    for (const u of usageData.value) {
      if (u.appId !== appId || !u.startTime || !u.endTime) continue
      const fmt = formatTitle(u.appName, u.title)
      const key = fmt.primary
      const secs = Math.round((u.endTime.getTime() - u.startTime.getTime()) / 1000)
      const cur = byTitle.get(key) ?? { secondary: fmt.secondary, category: fmt.category, totalSeconds: 0, count: 0 }
      cur.totalSeconds += secs
      cur.count += 1
      byTitle.set(key, cur)
    }
    return [...byTitle.entries()]
      .map(([title, v]) => ({ title, secondary: v.secondary, category: v.category, totalSeconds: v.totalSeconds, count: v.count }))
      .sort((a, b) => b.totalSeconds - a.totalSeconds)
  }

  return {
    usageData,
    includeAway,
    appSummaries,
    awaySeconds,
    usageSeconds,
    totalSeconds,
    maxSeconds,
    weeklyAppSummaries,
    weeklyAwaySeconds,
    weeklyTotalSeconds,
    activeHours,
    titleBreakdown,
    loadUsage,
    loadDaily,
    loadWeekly,
  }
}
