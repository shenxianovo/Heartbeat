// Service worker：chrome 事件 → 折叠纯函数 → 队列 → 周期上报 loopback hub。
//
// MV3 SW 随时可能被杀：折叠状态存 chrome.storage.session（浏览器会话内跨 SW 重启存活，
// 浏览器退出即清——进行中活动的快照已生长到最后一次 flush，行自然封口，损失 ≤ 一个上报周期）。
// 待传队列存 chrome.storage.local（跨浏览器重启存活，Agent 未运行时不丢数据）。

import {
  applyEvent,
  emptyState,
  flush,
  type FoldDeps,
  type FoldEvent,
  type FoldState,
  type SegmentSnapshot,
} from './fold'
import { domainOf, identityKeyOf } from './normalize'
import { uuidv7 } from './ids'
import { postToHub, fetchCollectorConfig } from './hub'
import { loadConfig } from './config'
import { backoffAfterFailure, noBackoff, shouldSkipAttempt, type BackoffState } from './backoff'

/** chrome.alarms 最小周期 30s（Chrome 120+），与 manifest.minimum_chrome_version 对应。 */
const FLUSH_PERIOD_MINUTES = 0.5
/** flush 周期毫秒值（= FLUSH_PERIOD_MINUTES）：自报给 hub，hub 据此派生 Active 窗口（ADR-026 §3）。 */
const FLUSH_PERIOD_MS = FLUSH_PERIOD_MINUTES * 60_000
/** 本采集器的 Source 名（ADR-017）：与 hub 注册表 key、段的 source 字段一致。 */
const SOURCE = 'browser'
/** 队列按 Id 键控压缩后仍超上限时丢最旧（失控保险，镜像 SegmentIngestService.MaxBuffered 思路）。 */
const MAX_QUEUED = 5000

const STATE_KEY = 'foldState'
const QUEUE_KEY = 'pendingSegments'
const BACKOFF_KEY = 'backoff'
const HUB_PORT_KEY = 'hubPort'
const ALARM_NAME = 'heartbeat-flush'

const deps: FoldDeps = {
  newId: uuidv7,
  identityKeyOf,
  domainOf,
  appName: detectAppName(),
}

/** 关联提示（ADR-017 §2）：对齐 system 采集器的 Process.ProcessName（不含 .exe），命中同一 App 行。 */
function detectAppName(): string {
  const brands: string[] =
    (navigator as unknown as { userAgentData?: { brands?: { brand: string }[] } }).userAgentData?.brands?.map(
      (b) => b.brand,
    ) ?? []
  if (brands.some((b) => b.includes('Edge'))) return 'msedge'
  if (brands.some((b) => b.includes('Brave'))) return 'brave'
  if (brands.some((b) => b.includes('Opera'))) return 'opera'
  return 'chrome'
}

// ---- 串行化：storage 读改写不可交错（事件处理与 flush 共享折叠状态）。----

let chain: Promise<unknown> = Promise.resolve()

function serialized<T>(fn: () => Promise<T>): Promise<T> {
  const next = chain.then(fn, fn)
  chain = next.catch(() => {})
  return next
}

// ---- 存储 ----

async function loadState(): Promise<FoldState> {
  const got = await chrome.storage.session.get(STATE_KEY)
  return (got[STATE_KEY] as FoldState | undefined) ?? emptyState()
}

async function saveState(state: FoldState): Promise<void> {
  await chrome.storage.session.set({ [STATE_KEY]: state })
}

async function loadQueue(): Promise<Record<string, SegmentSnapshot>> {
  const got = await chrome.storage.local.get(QUEUE_KEY)
  return (got[QUEUE_KEY] as Record<string, SegmentSnapshot> | undefined) ?? {}
}

async function saveQueue(queue: Record<string, SegmentSnapshot>): Promise<void> {
  await chrome.storage.local.set({ [QUEUE_KEY]: queue })
}

async function loadBackoff(): Promise<BackoffState> {
  const got = await chrome.storage.session.get(BACKOFF_KEY)
  return (got[BACKOFF_KEY] as BackoffState | undefined) ?? noBackoff
}

async function saveBackoff(state: BackoffState): Promise<void> {
  await chrome.storage.session.set({ [BACKOFF_KEY]: state })
}

/** hub 实际端口缓存（session：hub 顺延是运行时状态，浏览器重启后从基准端口重来）。 */
async function loadHubPort(basePort: number): Promise<number> {
  const got = await chrome.storage.session.get(HUB_PORT_KEY)
  const port = Number(got[HUB_PORT_KEY])
  return Number.isInteger(port) && port >= basePort ? port : basePort
}

async function saveHubPort(port: number): Promise<void> {
  await chrome.storage.session.set({ [HUB_PORT_KEY]: port })
}

/** 入队按 Id 键控：同段后到快照覆盖先到（快照单调生长，攒批自动压缩，ADR-018）。 */
async function enqueue(snapshots: SegmentSnapshot[]): Promise<void> {
  if (snapshots.length === 0) return
  const queue = await loadQueue()
  for (const s of snapshots) queue[s.id] = s

  const ids = Object.keys(queue)
  if (ids.length > MAX_QUEUED) {
    const byOldest = Object.values(queue).sort((a, b) => a.startTime.localeCompare(b.startTime))
    for (const victim of byOldest.slice(0, ids.length - MAX_QUEUED)) {
      delete queue[victim.id]
      console.warn(`[heartbeat] 队列已满（${MAX_QUEUED}），丢弃最旧段 ${victim.id}`)
    }
  }
  await saveQueue(queue)
}

// ---- 事件处理 ----

async function handleEvent(ev: FoldEvent): Promise<void> {
  const state = await loadState()
  const { state: next, out } = applyEvent(state, ev, deps)
  if (next !== state) await saveState(next)
  await enqueue(out)
}

async function flushAndUpload(): Promise<void> {
  const state = await loadState()
  const { state: next, out } = flush(state, Date.now(), deps)
  if (next !== state) await saveState(next)
  await enqueue(out)

  // 退避门：hub 连续不可达时拉开尝试间隔（快照照常入队，不丢）。
  const backoff = await loadBackoff()
  const now = Date.now()
  if (shouldSkipAttempt(backoff, now)) return

  const { port: basePort } = await loadConfig()
  const targetPort = await loadHubPort(basePort)

  // 礼貌层停用（ADR-026 §4）：每轮 flush 拉一次 hub 侧配置——此调用同时是注册
  // （首次触达即"已安装"）与 flushPeriodMs 自报。enabled:false 则丢队列、不上报，
  // 免去注定被 403 的无效 POST；拉取失败（hub 不在/端口漂移）保守视为未停用。
  const collectorConfig = await fetchCollectorConfig(targetPort, SOURCE, FLUSH_PERIOD_MS)
  if (collectorConfig?.enabled === false) {
    const queued = Object.keys(await loadQueue()).length
    if (queued > 0) {
      console.warn(`[heartbeat] 采集器已在 hub 停用，丢弃 ${queued} 条段`)
      await saveQueue({})
    }
    return
  }

  const queue = await loadQueue()
  const items = Object.values(queue)
  if (items.length === 0) return

  const { result, port } = await postToHub(basePort, targetPort, items)
  if (port !== targetPort) await saveHubPort(port)

  if (result === 'ok') {
    await saveQueue({})
    if (backoff.fails > 0) await saveBackoff(noBackoff)
  } else if (result === 'rejected') {
    // 毒批次整批丢弃：hub 明确拒绝的数据重传无意义（含 403 = 被停用，强制层兜底）。
    // 身份已由 postToHub 确认——陌生服务的 4xx 不会走到这里。
    console.warn(`[heartbeat] hub 拒收 ${items.length} 条段，丢弃`)
    await saveQueue({})
    if (backoff.fails > 0) await saveBackoff(noBackoff)
  } else {
    // unreachable：保留队列，指数退避后重试（Agent 未运行时数据在 storage.local 缓冲）。
    await saveBackoff(backoffAfterFailure(backoff, now))
  }
}

/**
 * SW 唤醒对账：以"当前各窗口的 active tab"为真源重放一次。
 * 幂等——同 identityKey 不产生边界；已消失窗口的活动就地封口。
 */
async function reconcile(): Promise<void> {
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
  if (alarm.name === ALARM_NAME) void serialized(flushAndUpload)
})

// 每次 SW 唤醒都执行（幂等）：确保闹钟存在 + 状态对账。
chrome.alarms.create(ALARM_NAME, { periodInMinutes: FLUSH_PERIOD_MINUTES })
void serialized(reconcile)
