// 将窗口活动变化转换为 Segment 快照；观测规则由 window-activity 判定，无 chrome API 依赖。
//
// - 每个窗口各记其 active tab（忠实记录，不判操作系统前台；双时间线公理，ADR-017）。
//   多窗口时多段并存合法，windowId 进 Attributes 区分。
// - 进行中的活动持有稳定 Id：flush 推送 EndTime 单调生长的快照，
//   服务端按 Id upsert 收敛（ADR-018），跨上报周期不碎裂。
// - 单段生长逼近服务端 MaxDuration（24h）时轮换新 Id，防快照被校验丢弃。

import { observeWindow, type WindowActivity, type WindowObservation } from './window-activity'
import { domainOf, siteOf } from './normalize'
import type { SegmentSdk, SegmentState, SegmentSnapshot as SdkSnapshot } from './sdk/segments'

export type OpenActivity = SegmentState<WindowActivity>

/** 可 JSON 序列化的全量状态（存 chrome.storage.session，SW 重启不丢）。 */
export interface FoldState {
  open: Record<number, OpenActivity>
}

/** Browser 只定义事实内容，SDK 补齐身份及时间。 */
export interface BrowserPayload {
  identityKey: string
  title: string
  attributes: { url: string; domain: string; site: string; windowId: number }
}

export type SegmentSnapshot = SdkSnapshot<BrowserPayload, 'browser'>
export type FoldEvent = WindowObservation & { at: number }

export interface FoldDeps {
  segments: SegmentSdk<WindowActivity, BrowserPayload, 'browser'>
  identityKeyOf: (url: string) => string
}

export function browserPayloadOf(activity: WindowActivity): BrowserPayload {
  return {
    identityKey: activity.identityKey,
    title: activity.title,
    attributes: {
      url: activity.url, domain: domainOf(activity.url), site: siteOf(activity.url), windowId: activity.windowId,
    },
  }
}

export interface FoldResult {
  state: FoldState
  out: SegmentSnapshot[]
}

export function emptyState(): FoldState {
  return { open: {} }
}

export function applyEvent(state: FoldState, ev: FoldEvent, deps: FoldDeps): FoldResult {
  const cur = state.open[ev.windowId]
  const change = observeWindow(cur, ev, deps.identityKeyOf)

  if (change.kind === 'closed') {
    if (!cur) return { state, out: [] }
    const open = { ...state.open }
    delete open[ev.windowId]
    return { state: { open }, out: [deps.segments.restore(cur).end(ev.at)] }
  }

  // 活动连续时保留 Fact 身份与起点，最新读数随下一次快照上行。
  if (change.kind === 'updated' && cur) {
    const open = { ...state.open, [ev.windowId]: deps.segments.restore(cur).update(change.activity).state }
    return { state: { open }, out: [] }
  }

  const out = cur ? [deps.segments.restore(cur).end(ev.at)] : []
  const next = deps.segments.startSegment({ start: ev.at, payload: change.activity }).state
  return { state: { open: { ...state.open, [ev.windowId]: next } }, out }
}

/** Browser 的事件订阅仍在观察这些窗口；将已观测时刻交给各自的 Segment。 */
export function flush(state: FoldState, observedUntil: number, deps: FoldDeps): FoldResult {
  const out: SegmentSnapshot[] = []
  let open = state.open

  for (const [wid, activity] of Object.entries(state.open)) {
    const segment = deps.segments.restore(activity)
    out.push(...segment.observe(observedUntil))
    if (segment.state !== activity) {
      if (open === state.open) open = { ...open }
      open[Number(wid)] = segment.state
    }
  }

  return { state: open === state.open ? state : { open }, out }
}
