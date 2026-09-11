// DTO → 模型输入的适配层（ADR-019 家族：labelUpgrade / replayModel 的入口在此归位）。
// 输入是结构化最小形状，SegmentResponse / AppUsageResponse 结构兼容。

import type { PluginSeg, SystemSeg } from './labelUpgrade'
import type { ReplaySeg } from './timeline/replayModel'
import { intersectInterval, type Interval } from './timeline/timelineModel'

export interface SegmentLike {
  streamId?: string
  origin?: string
  source?: string
  aspect?: string
  identityKey?: string
  title?: string
  payload?: Record<string, unknown>
  startTime?: Date
  endTime?: Date
}

export interface UsageSegLike {
  source?: string
  aspect?: string
  appKey?: string
  appDisplayName?: string
  appName?: string
  title?: string
  startTime?: Date
  endTime?: Date
}

/** Fact payload 是结构化对象；历史数据在 Analytics 迁移时归一为相同形状。 */
function attributesOf(payload?: Record<string, unknown>): Record<string, unknown> | undefined {
  const attributes = payload?.attributes
  return attributes && typeof attributes === 'object' && !Array.isArray(attributes)
    ? attributes as Record<string, unknown>
    : undefined
}

export function urlOf(payload?: Record<string, unknown>): string | undefined {
  const url = attributesOf(payload)?.url
  return typeof url === 'string' ? url : undefined
}

export function laneKeyOf(aspect: string | undefined, payload?: Record<string, unknown>, streamId?: string): string | undefined {
  if (!aspect) return undefined
  const attributes = attributesOf(payload)
  const laneKey = aspect === 'selected-page' ? attributes?.windowId : attributes?.laneKey
  if (typeof laneKey !== 'number' && typeof laneKey !== 'string') return undefined
  // Browser window IDs are local to an External Host. Legacy imports cannot recover that host.
  if (aspect === 'selected-page') return streamId ? `${streamId}:${laneKey}` : undefined
  return streamId ? `${streamId}:${laneKey}` : String(laneKey)
}

function boundedSpan(start: number, end: number, window?: Interval): Interval | null {
  if (!window) return end >= start ? { start, end } : null
  if (end === start) {
    return start >= window.start && start < window.end ? { start, end } : null
  }
  return intersectInterval({ start, end }, window)
}

/** labelUpgrade 输入：插件段。 */
export function toPluginSegs(segments: SegmentLike[], window?: Interval): PluginSeg[] {
  return segments
    .filter(s => s.startTime && s.endTime)
    .flatMap(s => {
      const span = boundedSpan(s.startTime!.getTime(), s.endTime!.getTime(), window)
      return span ? [{
        ...span,
        identityKey: s.identityKey,
        title: s.title ?? undefined,
        url: urlOf(s.payload),
      }] : []
    })
}

/** labelUpgrade 输入：system 段。 */
export function toSystemSegs(usage: UsageSegLike[], window?: Interval): SystemSeg[] {
  return usage
    .filter(u => u.startTime && u.endTime)
    .flatMap(u => {
      const span = boundedSpan(u.startTime!.getTime(), u.endTime!.getTime(), window)
      return span ? [{
        ...span,
        // Title Formatter 按稳定产品 Key 路由；DisplayName 只承担呈现。
        appName: u.appKey ?? u.appDisplayName ?? u.appName,
        title: u.title ?? undefined,
      }] : []
    })
}

/** replayModel 输入：system 主轨在前，插件段带 laneKey（副本泳道）。 */
export function toReplaySegs(
  usage: UsageSegLike[],
  segments: SegmentLike[],
  window?: Interval,
): ReplaySeg[] {
  const out: ReplaySeg[] = []
  for (const u of usage) {
    if (!u.startTime || !u.endTime) continue
    const span = boundedSpan(u.startTime.getTime(), u.endTime.getTime(), window)
    if (!span) continue
    out.push({
      ...span,
      source: u.source ?? '',
      aspect: u.aspect,
      label: u.title ?? '',
    })
  }
  for (const s of segments) {
    if (!s.startTime || !s.endTime || !s.source) continue
    const span = boundedSpan(s.startTime.getTime(), s.endTime.getTime(), window)
    if (!span) continue
    // 回放呈现标题与原始 URL，Fact 信封和运输元数据不进入产品 tooltip。
    out.push({
      ...span,
      source: s.source,
      aspect: s.aspect,
      label: [s.title ?? s.identityKey, urlOf(s.payload)].filter(Boolean).join('  '),
      laneKey: laneKeyOf(s.aspect, s.payload, s.origin === 'legacy-import' ? undefined : s.streamId),
    })
  }
  return out
}
