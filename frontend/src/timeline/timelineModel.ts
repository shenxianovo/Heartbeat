// 主时间轴模型（纯函数，无 Vue / API client 依赖）：解析 → 缝合 → 排行 → 投影。
// 输入是结构化最小形状，AppUsageResponse 结构兼容。

import { isAwayApp } from '../appLabels'
import { fmtTime } from './timeScale'

export interface Interval {
  start: number
  end: number
}

export interface ProjectedInterval {
  left: number
  width: number
}

export interface UsageLike {
  appId?: number
  appKey?: string
  appDisplayName?: string
  appName?: string
  deviceId?: number
  startTime?: Date
  endTime?: Date
  durationSeconds?: number
}

export interface ParsedUsage {
  /** 每 App 的段区间：已按开始时间排序、≤MERGE_GAP_MS 的缝隙已缝合。 */
  byApp: Map<number, Interval[]>
  /** away（离开）段对应的 appId 集合 —— 由 appName 识别。 */
  awayAppIds: Set<number>
}

// 同一 App 相邻段合并阈值：标题切段首尾相接，仅 <1s 丢段会留小缝，
// ≤2s 缝合这些缝隙，又不会把真实切走别的 App 画成连续使用。
export const MERGE_GAP_MS = 2000

// 缩略图活动爆发的合并阈值：≤1min 的间断视为同一段活跃。
const BURST_MERGE_GAP_MS = 60_000

const ONE_HOUR = 60 * 60 * 1000

export function parseUsage(usage: UsageLike[]): ParsedUsage {
  const byApp = new Map<number, Interval[]>()
  const awayAppIds = new Set<number>()

  for (const u of usage) {
    if (!u.appId || !u.startTime || !u.endTime) continue
    if (isAwayApp(u.appKey, u.appDisplayName ?? u.appName)) awayAppIds.add(u.appId)
    let arr = byApp.get(u.appId)
    if (!arr) {
      arr = []
      byApp.set(u.appId, arr)
    }
    arr.push({ start: u.startTime.getTime(), end: u.endTime.getTime() })
  }

  // 每个 App 内按开始时间排序，缝隙 ≤MERGE_GAP_MS 的相邻段合并为一段（标题不同不切分）
  for (const [appId, segments] of byApp) {
    segments.sort((a, b) => a.start - b.start)
    const merged: Interval[] = []
    for (const seg of segments) {
      const last = merged[merged.length - 1]
      if (last && seg.start - last.end <= MERGE_GAP_MS) {
        last.end = Math.max(last.end, seg.end)
      } else {
        merged.push({ ...seg })
      }
    }
    byApp.set(appId, merged)
  }

  return { byApp, awayAppIds }
}

export interface RowBar {
  start: number
  end: number
  /** 视窗内投影位置（%），已钳位。 */
  left: number
  /** 视窗内投影宽度（%），最小 0.5 保证可见。 */
  width: number
  /** tooltip："HH:MM - HH:MM"。 */
  label: string
}

export interface TimelineRow {
  appId: number
  isAway: boolean
  bars: RowBar[]
}

/** 视窗内的行：按可见时长降序，段投影为百分比条。视窗外的 App 不出现。 */
export function buildRows(parsed: ParsedUsage, view: Interval): TimelineRow[] {
  const range = view.end - view.start
  if (range <= 0) return []

  const durations: [number, number][] = []
  for (const [appId, segments] of parsed.byApp) {
    let total = 0
    for (const seg of segments) {
      if (seg.end <= view.start || seg.start >= view.end) continue
      total += Math.min(seg.end, view.end) - Math.max(seg.start, view.start)
    }
    if (total > 0) durations.push([appId, total])
  }
  durations.sort((a, b) => b[1] - a[1])

  return durations.map(([appId]) => {
    const bars: RowBar[] = []
    for (const seg of parsed.byApp.get(appId)!) {
      if (seg.end <= view.start || seg.start >= view.end) continue
      const l = Math.max(0, Math.min(100, ((seg.start - view.start) / range) * 100))
      const r = Math.max(0, Math.min(100, ((seg.end - view.start) / range) * 100))
      bars.push({
        start: seg.start,
        end: seg.end,
        left: l,
        width: Math.max(0.5, r - l),
        label: `${fmtTime(seg.start)} - ${fmtTime(seg.end)}`,
      })
    }
    return { appId, isAway: parsed.awayAppIds.has(appId), bars }
  })
}

/** 把区间按半开语义裁剪并投影到窗口；只碰到任一端点不算可见。 */
export function projectInterval(interval: Interval, window: Interval): ProjectedInterval | null {
  const range = window.end - window.start
  if (range <= 0 || interval.end <= window.start || interval.start >= window.end) return null

  const start = Math.max(interval.start, window.start)
  const end = Math.min(interval.end, window.end)
  return {
    left: ((start - window.start) / range) * 100,
    width: ((end - start) / range) * 100,
  }
}

export interface DayHourBin extends Interval {
  label: string
  active: boolean
}

/**
 * 简略时间线按真实 instant 小时铺格：spring-forward 为 23 格，fall-back 为 25 格；
 * civil label 使用刷新时捕获的 timezone，因此回拨日的两个 01:00 保持独立 instant。
 */
export function buildDayHourBins(
  day: Interval,
  usage: UsageLike[],
  timeZone: string,
): DayHourBin[] {
  if (day.end <= day.start) return []

  const active = usage
    .filter(item => item.startTime && item.endTime)
    .filter(item => !isAwayApp(item.appKey, item.appDisplayName ?? item.appName))
    .map(item => ({ start: item.startTime!.getTime(), end: item.endTime!.getTime() }))
    .filter(interval => interval.end > interval.start)

  const bins: DayHourBin[] = []
  for (let start = day.start; start < day.end; start += ONE_HOUR) {
    const end = Math.min(start + ONE_HOUR, day.end)
    bins.push({
      start,
      end,
      label: fmtTime(start, timeZone),
      active: active.some(interval => interval.end > start && interval.start < end),
    })
  }
  return bins
}

/** 缩略图用的活动爆发区间：全 App 合并（away 不算活跃），间断 ≤1min 缝合。 */
export function mergeActivityBursts(parsed: ParsedUsage): Interval[] {
  const raw: Interval[] = []
  for (const [appId, segments] of parsed.byApp) {
    if (parsed.awayAppIds.has(appId)) continue
    for (const seg of segments) raw.push(seg)
  }
  raw.sort((a, b) => a.start - b.start)
  if (raw.length === 0) return []

  const merged: Interval[] = []
  let current = { ...raw[0] }
  for (let i = 1; i < raw.length; i++) {
    const next = raw[i]
    if (next.start <= current.end + BURST_MERGE_GAP_MS) {
      current.end = Math.max(current.end, next.end)
    } else {
      merged.push(current)
      current = { ...next }
    }
  }
  merged.push(current)
  return merged
}

/**
 * 在线时长（秒）：全部真实使用段（away 不算）的严格区间并集。
 * 聚合视图的"今天在多久"主数字——两台设备同时使用只算一份墙钟时间，
 * 与按设备求和的"屏幕占用"（可 >24h）语义互补。不缝合缝隙：不在就是不在。
 */
export function onlineUnionSeconds(usage: UsageLike[]): number {
  const raw: Interval[] = []
  for (const u of usage) {
    if (!u.startTime || !u.endTime || isAwayApp(u.appKey, u.appDisplayName ?? u.appName)) continue
    const start = u.startTime.getTime()
    const end = u.endTime.getTime()
    if (end > start) raw.push({ start, end })
  }
  raw.sort((a, b) => a.start - b.start)

  let total = 0
  let curStart = 0
  let curEnd = -1
  for (const iv of raw) {
    if (iv.start > curEnd) {
      total += curEnd - curStart
      curStart = iv.start
      curEnd = iv.end
    } else {
      curEnd = Math.max(curEnd, iv.end)
    }
  }
  total += curEnd - curStart
  return Math.round(Math.max(0, total) / 1000)
}

/**
 * 按设备分组（聚合视图的泳道键）：保持组内原始顺序，
 * 组间按首段开始时间升序——先醒的设备排前面。缺 deviceId 的段归入 0 组（旧数据兜底）。
 */
export function groupByDevice(usage: UsageLike[]): Map<number, UsageLike[]> {
  const groups = new Map<number, UsageLike[]>()
  for (const u of usage) {
    const key = u.deviceId ?? 0
    let arr = groups.get(key)
    if (!arr) {
      arr = []
      groups.set(key, arr)
    }
    arr.push(u)
  }

  const firstStart = (arr: UsageLike[]) =>
    arr.reduce((min, u) => Math.min(min, u.startTime?.getTime() ?? Infinity), Infinity)
  return new Map([...groups.entries()].sort((a, b) => firstStart(a[1]) - firstStart(b[1])))
}

/**
 * 初始视窗：今天以 now 为中心 ±1h；历史日以首个事件为中心 ±1h；
 * 无数据时取精确日窗中点 ±1h。所有路径都钳在 day endpoints 内。
 */
export function initialViewBounds(
  day: Interval,
  isToday: boolean,
  usage: UsageLike[],
  now: number,
): Interval {
  const dayRange = day.end - day.start
  if (dayRange <= 0) return { ...day }

  const firstEvent = usage.find(item => item.startTime)?.startTime?.getTime()
  const center = isToday
    ? now
    : firstEvent ?? (day.start + day.end) / 2
  const range = Math.min(2 * ONE_HOUR, dayRange)
  let start = center - range / 2
  let end = center + range / 2

  if (start < day.start) {
    start = day.start
    end = start + range
  }
  if (end > day.end) {
    end = day.end
    start = end - range
  }
  return { start, end }
}
