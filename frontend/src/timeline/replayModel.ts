// 回放展开态模型（AppDetailModal，纯函数）：包络视窗、按 Source 分轨、轨内按副本分 lane。
// 多副本（如多浏览器窗口）的段合法重叠——有副本身份按身份分稳定泳道，
// 无身份贪心装箱兜底；system 前台互斥，恒为单 lane。

import { fmtTime } from './timeScale'
import { intersectInterval, type Interval } from './timelineModel'

export interface ReplaySeg {
  start: number
  end: number
  source: string
  /** tooltip 主体（时间前缀由模型拼接）。 */
  label: string
  /** 通用副本身份：有则稳定泳道，无则装箱兜底。 */
  laneKey?: string
}

export interface TrackBar {
  left: number
  width: number
  tooltip: string
  isPoint: boolean
}

export interface Lane {
  key?: string
  bars: TrackBar[]
}

export interface Track {
  source: string
  lanes: Lane[]
}

/** 点事件判定阈值：<1s 视为零长点事件（ADR-017），渲染为菱形。 */
const POINT_THRESHOLD_MS = 1000

function clipToView(seg: ReplaySeg, view: Interval): ReplaySeg | null {
  const isPoint = seg.end - seg.start < POINT_THRESHOLD_MS
  if (isPoint) {
    return seg.start >= view.start && seg.start < view.end ? seg : null
  }
  const visible = intersectInterval(seg, view)
  if (!visible) return null
  return {
    ...seg,
    ...visible,
  }
}

/** 全部区间的时间包络，前后各 pad padRatio（下限 minPadMs）；无有效跨度返回 null。 */
export function envelope(
  intervals: Interval[],
  padRatio = 0.03,
  minPadMs = 60_000,
): Interval | null {
  let min = Infinity
  let max = -Infinity
  for (const iv of intervals) {
    min = Math.min(min, iv.start)
    max = Math.max(max, iv.end)
  }
  if (!isFinite(min) || max <= min) return null
  const pad = Math.max((max - min) * padRatio, minPadMs)
  return { start: min - pad, end: max + pad }
}

function toBar(seg: ReplaySeg, view: Interval, timeZone: string): TrackBar {
  const range = view.end - view.start
  const left = ((seg.start - view.start) / range) * 100
  const isPoint = seg.end - seg.start < POINT_THRESHOLD_MS
  const width = isPoint ? 0 : Math.max(0.4, ((seg.end - seg.start) / range) * 100)
  const time = isPoint
    ? fmtTime(seg.start, timeZone)
    : `${fmtTime(seg.start, timeZone)} - ${fmtTime(seg.end, timeZone)}`
  return { left, width, tooltip: seg.label ? `${time}  ${seg.label}` : time, isPoint }
}

/** 轨内分 lane：有 laneKey 按身份聚（稳定叙事泳道），无身份贪心装箱；lane 按首段时间排序。 */
function assignLanes(segs: ReplaySeg[]): ReplaySeg[][] {
  const keyed = new Map<string, ReplaySeg[]>()
  const pool: ReplaySeg[] = []
  for (const s of segs) {
    if (s.laneKey !== undefined) {
      let arr = keyed.get(s.laneKey)
      if (!arr) {
        arr = []
        keyed.set(s.laneKey, arr)
      }
      arr.push(s)
    } else {
      pool.push(s)
    }
  }

  const lanes: ReplaySeg[][] = [...keyed.values()]

  // 装箱：塞进"最后一段结束 ≤ 它开始"的第一条兜底 lane，塞不进开新 lane
  pool.sort((a, b) => a.start - b.start)
  const packed: ReplaySeg[][] = []
  for (const s of pool) {
    const lane = packed.find(l => l[l.length - 1].end <= s.start)
    if (lane) lane.push(s)
    else packed.push([s])
  }
  lanes.push(...packed)

  for (const lane of lanes) lane.sort((a, b) => a.start - b.start)
  lanes.sort((a, b) => a[0].start - b[0].start)
  return lanes
}

/** 按 Source 分轨（保持输入相遇顺序：调用方 system 在前），轨内分 lane 并投影。 */
export function buildTracks(segs: ReplaySeg[], view: Interval, timeZone: string): Track[] {
  if (view.end - view.start <= 0) return []

  const bySource = new Map<string, ReplaySeg[]>()
  for (const s of segs) {
    const visible = clipToView(s, view)
    if (!visible) continue
    let arr = bySource.get(visible.source)
    if (!arr) {
      arr = []
      bySource.set(visible.source, arr)
    }
    arr.push(visible)
  }

  const tracks: Track[] = []
  for (const [source, group] of bySource) {
    const lanes = assignLanes(group).map(lane => ({
      key: lane[0].laneKey,
      bars: lane.map(s => toBar(s, view, timeZone)),
    }))
    tracks.push({ source, lanes })
  }
  return tracks
}
