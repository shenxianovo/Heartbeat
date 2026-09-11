// Service worker：chrome 事件 → 折叠纯函数 → 队列 → 周期上报 loopback hub。
//
// Fold checkpoints and outbox snapshots share one durable journal; no window-close or revision
// can commit independently of its delivery responsibility. Session state only distinguishes a
// suspended worker from a complete browser restart.

import {
  applyEvent,
  browserPayloadOf,
  emptyState,
  flush,
  type FoldDeps,
  type FoldEvent,
  type FoldState,
  type SegmentSnapshot,
} from './fold'
import { activityKeyOf } from './normalize'
import { createSegmentSdk } from './sdk/segments'
import { ChromeBrowserDeliveryStore, createChromeBrowserDelivery } from './delivery-chrome'
import { snapshotRevision } from './protocol'
import type { BrowserCollectionPolicy } from './delivery'
import { isDevelopment, reloadDevelopmentExtensionIfUpdated } from './connection'

const SESSION_MARKER_KEY = 'browserObservationSessionStarted'
const ALARM_NAME = 'heartbeat-flush'
const DEVELOPMENT_ALARM = 'heartbeat-development-update'

const deps: FoldDeps = {
  segments: createSegmentSdk({ source: 'browser', payloadOf: browserPayloadOf }),
  activityKeyOf,
}

const store = new ChromeBrowserDeliveryStore()
const delivery = createChromeBrowserDelivery(store)
let browserSessionReady = false

// ---- 串行化：storage 读改写不可交错（事件处理与 flush 共享折叠状态）。----

let chain: Promise<unknown> = Promise.resolve()

function serialized<T>(fn: () => Promise<T>): Promise<T> {
  const next = chain.then(fn, fn)
  chain = next.catch(() => {})
  return next
}

// ---- 折叠状态（交付持久化由 BrowserDelivery 拥有）----

async function loadState(): Promise<FoldState> {
  return (await store.loadDurable()).foldState ?? emptyState()
}

async function finishKnownActivity(knownState?: FoldState): Promise<void> {
  const state = knownState ?? await loadState()
  const open = { ...state.open }
  const final: SegmentSnapshot[] = []
  for (const [windowId, activity] of Object.entries(open)) {
    if (activity.recoveryRequired) continue
    const last = activity.lastSnapshot as SegmentSnapshot | undefined
    if (last !== undefined) {
      final.push(last.isFinal ? last : { ...last, isFinal: true, revision: snapshotRevision(last) + 1 })
    } else if (activity.kind === 'segment') {
      // We know the start observation, but cannot invent an observed end across a browser stop.
      final.push(deps.segments.restore(activity).end(activity.startTime))
    } else {
      open[Number(windowId)] = { ...activity, recoveryRequired: true }
      continue
    }
    delete open[Number(windowId)]
  }
  await delivery.checkpoint(final, { open })
}

async function resumeBrowserSession(): Promise<void> {
  const session = await chrome.storage.session.get([SESSION_MARKER_KEY, 'foldState'])
  const state = await loadState()
  if (session[SESSION_MARKER_KEY] !== true && session.foldState === undefined) {
    // A full browser restart cannot establish observations during the stopped interval.
    await finishKnownActivity(state)
  }
  await chrome.storage.session.set({ [SESSION_MARKER_KEY]: true })
  // The legacy fold remains in the immutable migration backup, never as a second live authority.
  await chrome.storage.session.remove('foldState')
  browserSessionReady = true
}

async function tryResumeBrowserSession(): Promise<boolean> {
  if (browserSessionReady) return true
  try { await resumeBrowserSession() }
  catch (error) { console.warn('[heartbeat] Browser recovery checkpoint retained for retry', error) }
  return browserSessionReady
}

function flushRecoverable(state: FoldState, at: number) {
  const waiting = Object.fromEntries(Object.entries(state.open).filter(([, activity]) => activity.recoveryRequired))
  const ready = Object.fromEntries(Object.entries(state.open).filter(([, activity]) => !activity.recoveryRequired))
  const result = flush({ open: ready }, at, deps)
  return { ...result, state: { open: { ...waiting, ...result.state.open } } }
}

// ---- 事件处理 ----

async function handleEvent(ev: FoldEvent): Promise<void> {
  if (!browserSessionReady || !(await delivery.policy()).enabled) return
  const state = await loadState()
  if (state.open[ev.windowId]?.recoveryRequired) return
  const { state: next, out } = applyEvent(state, ev, deps)
  await delivery.checkpoint(out, next)
}

async function flushAndUpload(): Promise<void> {
  const before = await delivery.policy()
  if (before.enabled && await tryResumeBrowserSession()) {
    try { await persistActivity() }
    catch (error) {
      // The previous fold/outbox checkpoint still owns responsibility. Existing pending facts
      // must be allowed to drain before the next cycle retries this observation.
      console.warn('[heartbeat] Activity checkpoint retained for retry', error)
    }
  }
  const after = await delivery.deliveryCycle()
  if (!await tryResumeBrowserSession()) return
  await applyDeliveryPolicy(before, after)
  if (after.enabled) await reconcile()
}

async function persistActivity(): Promise<void> {
  const state = await loadState()
  const { state: next, out } = flushRecoverable(state, Date.now())
  await delivery.checkpoint(out, next)
}

async function reloadDevelopmentUpdate(): Promise<boolean> {
  if (!browserSessionReady || !isDevelopment()) return false
  try {
    return await reloadDevelopmentExtensionIfUpdated(async () => {
      if (!(await delivery.policy()).enabled) return
      const state = await loadState()
      const { state: next, out } = flushRecoverable(state, Date.now())
      await delivery.checkpoint(out, next)
    })
  } catch (error) {
    console.warn('Development update postponed; local state retained. Manual Reload may be needed.', error)
    return false
  }
}

async function applyDeliveryPolicy(
  before: BrowserCollectionPolicy,
  after: BrowserCollectionPolicy,
): Promise<void> {
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: after.flushPeriodMilliseconds / 60_000,
  })
  if (!after.enabled) {
    // 已知停用跨浏览器重启保留；fold state 必须同时封死，outbox 则继续保留。
    await finishKnownActivity()
  } else if (!before.enabled) {
    // 重新启用从当前 tab 新开活动，不把停用区间补进旧 Segment。
    await finishKnownActivity()
    await reconcile()
  }
}

/**
 * SW 唤醒对账：以"当前各窗口的 active tab"为真源重放一次。
 * 幂等——同 activityKey 不产生边界；已消失窗口的活动就地封口。
 */
async function reconcile(): Promise<void> {
  if (!(await delivery.policy()).enabled) return
  const tabs = await chrome.tabs.query({ active: true })
  const liveWindows = new Set(tabs.map((t) => t.windowId))
  const now = Date.now()

  const state = await loadState()
  for (const wid of Object.keys(state.open).map(Number)) {
    if (!liveWindows.has(wid)) await handleEvent({ kind: 'windowClosed', windowId: wid, at: now })
  }
  for (const t of tabs) {
    if (t.url && t.windowId !== undefined) {
      await handleEvent({ kind: 'activated', windowId: t.windowId, url: t.url, title: t.title ?? '', at: now })
    }
  }
}

// ---- 接线（顶层同步注册，MV3 要求）----

chrome.tabs.onActivated.addListener(({ tabId, windowId }) => {
  void serialized(async () => {
    const tab = await chrome.tabs.get(tabId).catch(() => null)
    if (!tab?.url) return
    await handleEvent({ kind: 'activated', windowId, url: tab.url, title: tab.title ?? '', at: Date.now() })
  })
})

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  // 只关心"当前 active tab 的身份/标题变化"；后台 tab 的加载与本采集器无关。
  if (!tab.active || !tab.url) return
  if (changeInfo.url === undefined && changeInfo.title === undefined) return
  void serialized(() =>
    handleEvent({ kind: 'activated', windowId: tab.windowId, url: tab.url!, title: tab.title ?? '', at: Date.now() }),
  )
})

chrome.windows.onRemoved.addListener((windowId) => {
  void serialized(() => handleEvent({ kind: 'windowClosed', windowId, at: Date.now() }))
})

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === DEVELOPMENT_ALARM && isDevelopment()) void serialized(reloadDevelopmentUpdate)
  if (alarm.name === ALARM_NAME) void serialized(async () => {
    if (!(await reloadDevelopmentUpdate())) await flushAndUpload()
  })
})

// 每次 SW 唤醒都执行（幂等）：按持久 policy 恢复闹钟与 fold 状态，再对账。
void serialized(async () => {
  await tryResumeBrowserSession()
  if (isDevelopment()) {
    // Alarms wake suspended MV3 workers, including when collection itself is disabled.
    chrome.alarms.create(DEVELOPMENT_ALARM, { periodInMinutes: 0.5 })
    if (await reloadDevelopmentUpdate()) return
  }
  const current = await delivery.policy()
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: current.flushPeriodMilliseconds / 60_000,
  })
  if (!browserSessionReady) {
    // A full queue or temporary storage failure must not prevent installing the retry alarm.
    await delivery.deliveryCycle()
    if (!await tryResumeBrowserSession()) return
  }
  if (!(await delivery.policy()).enabled) await finishKnownActivity()
  else await reconcile()
  if (isDevelopment()) await flushAndUpload()
})
