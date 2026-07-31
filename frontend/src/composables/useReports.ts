import { ref, computed, type Ref } from 'vue'
import type { AppSummary, AppUsageResponse, DailyReportResponse, WeeklyReportResponse } from '../api/index'
import { fetchPublicUsage, fetchPublicDailyReport, fetchPublicWeeklyReport } from '../api/index'
import { useAsyncData } from './useAsyncData'
import { isAwayName } from '../appLabels'
import { onlineUnionSeconds, groupByDevice } from '../timeline/timelineModel'

interface AppDurationLike {
  appId?: number
  appName?: string
  durationSeconds?: number
}

/** away 政策收口处：从报表明细里分离真实应用与"离开"时间。 */
function realApps(apps: AppDurationLike[] | undefined): AppSummary[] {
  if (!apps) return []
  return apps
    .filter(a => !isAwayName(a.appName))
    .map(a => ({
      appId: a.appId!,
      appName: a.appName ?? `App ${a.appId}`,
      totalSeconds: a.durationSeconds!,
    }))
    .sort((a, b) => b.totalSeconds - a.totalSeconds)
}

function awayOf(apps: AppDurationLike[] | undefined): number {
  return apps?.find(a => isAwayName(a.appName))?.durationSeconds ?? 0
}

/**
 * 统计/回放域：日报、周报、原始用量段，以及由它们派生的排行、求和、活跃小时。
 * away 政策（includeAway 开关 + 过滤/求和）集中在此处，不散落到组件。
 * 标题明细已移至 AppDetailModal（ADR-019 标签升级，见 labelUpgrade.ts）。
 *
 * 多设备语义（deviceId=0 时跨设备聚合）：
 * - onlineSeconds（并集，滤 AWAY）答"我今天在多久"，主数字，恒 ≤ 墙钟
 * - totalSeconds（各设备求和）答"屏幕被占用多久"，副数字，双机并发时可超 24h
 * - awaySeconds 求和 = "设备空转"明细；includeAway 只作用于求和族，永不改并集
 * 不做的：跨设备注意力裁决、"全设备同时 away"交集、周并集（前端没有 7 天全量段）
 */
export function useReports(
  username: string,
  selectedDevice: Ref<number>,
  selectedDate: Ref<string>,
) {
  // usage 的 start/end 是时刻过滤(UTC 瞬间):所选日期的本地 0 点起 24 小时。
  function usageWindow() {
    const dateObj = new Date(selectedDate.value + 'T00:00:00')
    return {
      start: dateObj.toISOString(),
      end: new Date(dateObj.getTime() + 86400000).toISOString(),
    }
  }

  const usage = useAsyncData<AppUsageResponse[]>(
    () => fetchPublicUsage(username, { deviceId: selectedDevice.value, ...usageWindow() }),
    [],
  )
  const daily = useAsyncData<DailyReportResponse | null>(
    () => fetchPublicDailyReport(username, { deviceId: selectedDevice.value, date: selectedDate.value }),
    null,
  )
  const weekly = useAsyncData<WeeklyReportResponse | null>(
    () => fetchPublicWeeklyReport(username, { deviceId: selectedDevice.value, date: selectedDate.value }),
    null,
  )

  const usageData = usage.data
  const dailyReport = daily.data
  const weeklyReport = weekly.data
  const error = computed(() => usage.error.value ?? daily.error.value ?? weekly.error.value)

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

  // ── 在线并集（多设备主数字）──
  // 对当天已在手的 usage 段做区间并集：两台设备重叠的时间只算一次。
  // 纯前端派生，不需要服务端字段（与 activeHours 同族运算）。
  const onlineSeconds = computed(() => onlineUnionSeconds(usageData.value))

  /** 各设备的屏幕占用求和（明细层：谁贡献了多少）。按占用降序。 */
  const perDeviceSeconds = computed(() => {
    const rows: { deviceId: number; usageSeconds: number; awaySeconds: number }[] = []
    for (const [deviceId, segs] of groupByDevice(usageData.value)) {
      let usageSec = 0
      let awaySec = 0
      for (const s of segs) {
        const dur = s.durationSeconds ?? 0
        if (isAwayName(s.appName)) awaySec += dur
        else usageSec += dur
      }
      rows.push({ deviceId, usageSeconds: usageSec, awaySeconds: awaySec })
    }
    return rows.sort((a, b) => b.usageSeconds - a.usageSeconds)
  })

  /** 并集 < 求和 即存在跨设备并发使用（双机同时开着）。 */
  const hasConcurrentUse = computed(() => usageSeconds.value > onlineSeconds.value + 1)

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
      if (isAwayName(u.appName)) continue
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

  // 取数不再依赖"设备列表已就位"：deviceId=0 即聚合查询，API 边界会归一为不传参。
  async function loadUsage() {
    await usage.run()
  }

  async function loadDaily() {
    await daily.run()
  }

  async function loadWeekly() {
    await weekly.run()
  }

  return {
    usageData,
    error,
    includeAway,
    appSummaries,
    awaySeconds,
    usageSeconds,
    totalSeconds,
    onlineSeconds,
    perDeviceSeconds,
    hasConcurrentUse,
    maxSeconds,
    weeklyAppSummaries,
    weeklyAwaySeconds,
    weeklyTotalSeconds,
    activeHours,
    loadUsage,
    loadDaily,
    loadWeekly,
  }
}
