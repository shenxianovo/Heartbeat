// 主时间轴模型（纯函数，无 Vue / API client 依赖）：解析 → 缝合 → 排行 → 投影。
// 输入是结构化最小形状，AppUsageResponse 结构兼容。

import { isAwayName } from '../appLabels'
import { fmtTime } from './timeScale'

export interface Interval {
  start: number
  end: number
}

export interface UsageLike {
  appId?: number
  appName?: string
  startTime?: Date
  endTime?: Date
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
    if (isAwayName(u.appName)) awayAppIds.add(u.appId)
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
      if (seg.end < view.start || seg.start > view.end) continue
      total += Math.min(seg.end, view.end) - Math.max(seg.start, view.start)
    }
    if (total > 0) durations.push([appId, total])
  }
  durations.sort((a, b) => b[1] - a[1])

  return durations.map(([appId]) => {
    const bars: RowBar[] = []
    for (const seg of parsed.byApp.get(appId)!) {
      if (seg.end < view.start || seg.start > view.end) continue
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
 * 初始视窗：今天以 now 为中心 ±1h；历史日以首个事件为中心 ±1h；
 * 无数据时落在当日 11:00-13:00。now 作参数保持纯函数。
 */
export function initialViewBounds(
  selectedDate: string,
  isToday: boolean,
  usage: UsageLike[],
  now: number,
): Interval {
  if (isToday) {
    return { start: now - ONE_HOUR, end: now + ONE_HOUR }
  }
  const firstEvent = usage.find(u => u.startTime)
  if (firstEvent && firstEvent.startTime) {
    const t = firstEvent.startTime.getTime()
    return { start: t - ONE_HOUR, end: t + ONE_HOUR }
  }
  const baseTime = new Date(selectedDate).getTime()
  return { start: baseTime + 11 * ONE_HOUR, end: baseTime + 13 * ONE_HOUR }
}
