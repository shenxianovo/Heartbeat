// DTO → 模型输入的适配层（ADR-019 家族：labelUpgrade / replayModel 的入口在此归位）。
// 输入是结构化最小形状，SegmentResponse / AppUsageResponse 结构兼容。

import type { PluginSeg, SystemSeg } from './labelUpgrade'
import type { ReplaySeg } from './timeline/replayModel'

export interface SegmentLike {
  source?: string
  identityKey?: string
  title?: string
  attributes?: string
  startTime?: Date
  endTime?: Date
}

export interface UsageSegLike {
  appKey?: string
  appDisplayName?: string
  appName?: string
  title?: string
  startTime?: Date
  endTime?: Date
}

/** attributes 是各 source 自由结构的原始 JSON 串；解析失败一律返回 undefined。 */
function parseAttrs(attributes?: string): Record<string, unknown> | undefined {
  if (!attributes) return undefined
  try {
    const a: unknown = JSON.parse(attributes)
    return typeof a === 'object' && a !== null ? (a as Record<string, unknown>) : undefined
  } catch {
    return undefined
  }
}

/** 取 attributes.url 作副标签。 */
export function urlOf(attributes?: string): string | undefined {
  const url = parseAttrs(attributes)?.url
  return typeof url === 'string' ? url : undefined
}

/**
 * 按 source 的 laneKey 提取器注册表（titleFormatters 同款模式）：
 * 副本身份写在 Attributes 的哪个字段由各采集器自定，展示层在此登记。
 * 未登记的 source 返回 undefined → 回放泳道走装箱兜底。
 */
const LANE_KEY_EXTRACTORS: Record<string, (attrs: Record<string, unknown>) => string | undefined> = {
  // browser：每窗口各记其 active tab，windowId 进 Attributes（collection/collectors/Heartbeat.Collector.Browser/src/fold.ts）。
  // 注意 windowId 是浏览器会话内递增的，跨重启可能复用——同 lane 顺序排开，可读性无损。
  browser: a =>
    typeof a.windowId === 'number' || typeof a.windowId === 'string'
      ? String(a.windowId)
      : undefined,
}

export function laneKeyOf(source: string | undefined, attributes?: string): string | undefined {
  if (!source) return undefined
  const extract = LANE_KEY_EXTRACTORS[source.toLowerCase()]
  if (!extract) return undefined
  const attrs = parseAttrs(attributes)
  return attrs ? extract(attrs) : undefined
}

/** labelUpgrade 输入：插件段。 */
export function toPluginSegs(segments: SegmentLike[]): PluginSeg[] {
  return segments
    .filter(s => s.startTime && s.endTime)
    .map(s => ({
      start: s.startTime!.getTime(),
      end: s.endTime!.getTime(),
      identityKey: s.identityKey,
      title: s.title ?? undefined,
      url: urlOf(s.attributes),
    }))
}

/** labelUpgrade 输入：system 段。 */
export function toSystemSegs(usage: UsageSegLike[]): SystemSeg[] {
  return usage
    .filter(u => u.startTime && u.endTime)
    .map(u => ({
      start: u.startTime!.getTime(),
      end: u.endTime!.getTime(),
      // Title Formatter 按稳定产品 Key 路由；DisplayName 只承担呈现。
      appName: u.appKey ?? u.appDisplayName ?? u.appName,
      title: u.title ?? undefined,
    }))
}

/** replayModel 输入：system 主轨在前，插件段带 laneKey（副本泳道）。 */
export function toReplaySegs(usage: UsageSegLike[], segments: SegmentLike[]): ReplaySeg[] {
  const out: ReplaySeg[] = []
  for (const u of usage) {
    if (!u.startTime || !u.endTime) continue
    out.push({
      start: u.startTime.getTime(),
      end: u.endTime.getTime(),
      source: 'system',
      label: u.title ?? '',
    })
  }
  for (const s of segments) {
    if (!s.startTime || !s.endTime || !s.source) continue
    // attributes 原始 JSON 直接进 tooltip（v1 行为保持）
    out.push({
      start: s.startTime.getTime(),
      end: s.endTime.getTime(),
      source: s.source,
      label: [s.title ?? s.identityKey, s.attributes].filter(Boolean).join('  '),
      laneKey: laneKeyOf(s.source, s.attributes),
    })
  }
  return out
}
