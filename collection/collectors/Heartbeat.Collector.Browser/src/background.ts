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
import { domainOf, identityKeyOf, siteOf } from './normalize'
import { uuidv7 } from './ids'
import { findCompatibleHub, postToHub, fetchCollectorConfig, postDeclaration } from './hub'
import { loadConfig } from './config'
import { backoffAfterFailure, noBackoff, shouldSkipAttempt, type BackoffState } from './backoff'
import { detectBrowserAppHint } from './app-hint'
import { normalizeQueuedSnapshots } from './queue'
import {
  uploadWithBrowserProtocol,
  type BrowserProtocolSession,
} from './protocol'

/** chrome.alarms 最小周期 30s（Chrome 120+），与 manifest.minimum_chrome_version 对应。 */
const FLUSH_PERIOD_MINUTES = 0.5
/** flush 周期毫秒值（= FLUSH_PERIOD_MINUTES）：自报给 hub，hub 据此派生 Active 窗口（ADR-026 §3）。 */
const FLUSH_PERIOD_MS = FLUSH_PERIOD_MINUTES * 60_000
/** 本采集器的 Source 名（ADR-017）：与 hub 注册表 key、段的 source 字段一致。 */
const SOURCE = 'browser'

/**
 * 观测深度表声明（ADR-030 §1/§5）：本采集器的契约，读数命名归采集器主权。
 * from 指段的运输槽位；深度表变更（加层/挪层）才递增 version。
 * v2 提拔 site（可注册域,值空间层级 → 深度层）为最浅层——digest browser 轨长成
 * site → url → tab_title 三层树,判官粗档提案锚 site。服务端零改动,声明到即生效。
 */
const DECLARATION = {
  source: SOURCE,
  version: 2,
  layers: [
    { readings: [{ name: 'site', from: 'attributes.site', label: '站点' }] },
    { readings: [{ name: 'url', from: 'identityKey', label: '网址' }] },
    { readings: [{ name: 'tab_title', from: 'title', label: '标签页' }] },
  ],
} as const

const DECLARATION_ACK_KEY = 'declarationAckedVersion'

const STATE_KEY = 'foldState'
const QUEUE_KEY = 'pendingSegments'
const BACKOFF_KEY = 'backoff'
const HUB_PORT_KEY = 'hubPort'
const PROTOCOL_SESSION_KEY = 'collectorProtocolSession'
const DESIRED_ENABLED_KEY = 'browserCollectorDesiredEnabled'
const ALARM_NAME = 'heartbeat-flush'

const deps: FoldDeps = {
  newId: uuidv7,
  identityKeyOf,
  domainOf,
  siteOf,
  appHint: detectAppHint(),
}

/** 只报告逻辑产品；hub 的平台 adapter 再解析为 win:/mac: AppIdentity。 */
function detectAppHint(): string | undefined {
  const nav = navigator as unknown as {
    userAgent: string
    userAgentData?: { brands?: { brand: string }[] }
    brave?: { isBrave?: () => Promise<boolean> }
  }
  return detectBrowserAppHint({
    brands: nav.userAgentData?.brands?.map((b) => b.brand),
    userAgent: nav.userAgent,
    hasBraveApi: typeof nav.brave?.isBrave === 'function',
  })
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
  const stored =
    (got[QUEUE_KEY] as Record<string, SegmentSnapshot & { appName?: unknown }> | undefined) ?? {}

  // 扩展更新前缓存可能仍带 Windows appName。重放时去掉平台字段，并用当前宿主浏览器的
  // 逻辑 hint 补齐；若品牌不明确则省略 hint，段本身仍可由 hub 保留。
  return normalizeQueuedSnapshots(stored, deps.appHint)
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

async function loadProtocolSession(): Promise<BrowserProtocolSession | undefined> {
  const got = await chrome.storage.session.get(PROTOCOL_SESSION_KEY)
  return got[PROTOCOL_SESSION_KEY] as BrowserProtocolSession | undefined
}

async function saveProtocolSession(session: BrowserProtocolSession | undefined): Promise<void> {
  if (session === undefined) await chrome.storage.session.remove(PROTOCOL_SESSION_KEY)
  else await chrome.storage.session.set({ [PROTOCOL_SESSION_KEY]: session })
}

async function desiredEnabled(): Promise<boolean> {
  const got = await chrome.storage.session.get(DESIRED_ENABLED_KEY)
  return got[DESIRED_ENABLED_KEY] !== false
}

async function saveDesiredEnabled(enabled: boolean): Promise<void> {
  await chrome.storage.session.set({ [DESIRED_ENABLED_KEY]: enabled })
}

async function applyDesiredEnabled(enabled: boolean): Promise<void> {
  const wasEnabled = await desiredEnabled()
  await saveDesiredEnabled(enabled)
  if (!enabled) {
    // 结束本地 fold 会话但保留已生成且未 ACK 的 durable outbox。
    await saveState(emptyState())
  } else if (!wasEnabled) {
    // 重新启用从当前 tab 新开活动，避免把停用区间补进旧 Segment。
    await reconcile()
  }
}

/** 入队按 Id 键控：同段后到快照覆盖先到（快照单调生长，攒批自动压缩，ADR-018）。 */
async function enqueue(snapshots: SegmentSnapshot[]): Promise<void> {
  if (snapshots.length === 0) return
  const queue = await loadQueue()
  for (const s of snapshots) queue[s.id] = s
  await saveQueue(queue)
}

// ---- 事件处理 ----

async function handleEvent(ev: FoldEvent): Promise<void> {
  if (!await desiredEnabled()) return
  const state = await loadState()
  const { state: next, out } = applyEvent(state, ev, deps)
  if (next !== state) await saveState(next)
  await enqueue(out)
}

async function flushAndUpload(): Promise<void> {
  if (await desiredEnabled()) {
    const state = await loadState()
    const { state: next, out } = flush(state, Date.now(), deps)
    if (next !== state) await saveState(next)
    await enqueue(out)
  }

  // 退避门：hub 连续不可达时拉开尝试间隔（快照照常入队，不丢）。
  const backoff = await loadBackoff()
  const now = Date.now()
  if (shouldSkipAttempt(backoff, now)) return

  const { port: basePort } = await loadConfig()
  const targetPort = await loadHubPort(basePort)
  const compatiblePort = await findCompatibleHub(basePort, targetPort)
  if (compatiblePort === null) {
    await saveBackoff(backoffAfterFailure(backoff, now))
    return
  }
  if (compatiblePort !== targetPort) await saveHubPort(compatiblePort)

  // 声明上报（ADR-030 §3）：送达一次即闭嘴（ack 存 local,跨浏览器重启）;失败下轮再试,
  // 不阻塞段上报——声明缺席时服务端种子兜底,采集不受影响。
  const acked = await chrome.storage.local.get(DECLARATION_ACK_KEY)
  if (acked[DECLARATION_ACK_KEY] !== DECLARATION.version) {
    if (await postDeclaration(compatiblePort, DECLARATION))
      await chrome.storage.local.set({ [DECLARATION_ACK_KEY]: DECLARATION.version })
  }

  // 礼貌层停用（ADR-026 §4）：每轮 flush 拉一次 hub 侧配置——此调用同时是注册
  // （首次触达即"已安装"）与 flushPeriodMs 自报。enabled:false 则丢队列、不上报，
  // 免去注定被 403 的无效 POST；拉取失败（hub 不在/端口漂移）保守视为未停用。
  const collectorConfig = await fetchCollectorConfig(compatiblePort, SOURCE, FLUSH_PERIOD_MS)
  if (collectorConfig?.enabled === false) {
    await applyDesiredEnabled(false)
    return
  }
  if (collectorConfig?.enabled === true) await applyDesiredEnabled(true)

  const queue = await loadQueue()
  const items = Object.values(queue)
  if (items.length === 0) return

  const protocolResult = await uploadWithBrowserProtocol(
    compatiblePort,
    deps.appHint,
    items,
    await loadProtocolSession(),
  )

  if (protocolResult.kind === 'acked') {
    const acknowledged = new Set(protocolResult.acknowledgedIds)
    const remaining = Object.fromEntries(
      Object.entries(await loadQueue()).filter(([id]) => !acknowledged.has(id)),
    )
    await saveQueue(remaining)
    await saveProtocolSession(protocolResult.session)
    if (backoff.fails > 0) await saveBackoff(noBackoff)
    return
  }
  if (protocolResult.kind === 'disabled') {
    await applyDesiredEnabled(false)
    return
  }
  if (protocolResult.kind === 'unavailable') {
    await saveProtocolSession(undefined)
    await saveBackoff(backoffAfterFailure(backoff, now))
    return
  }

  // 旧缓存（非 UUIDv7）或旧 hub 明确要求 legacy adapter 时，维持原路由兼容。
  const { result, port } = await postToHub(basePort, compatiblePort, items)
  if (port !== compatiblePort) await saveHubPort(port)

  if (result === 'ok') {
    await saveQueue({})
    if (backoff.fails > 0) await saveBackoff(noBackoff)
  } else if (result === 'rejected') {
    // 未收到逐 Fact ACK，不删除 outbox；旧请求可以在升级完成后再次投递。
    console.warn(`[heartbeat] legacy hub 拒收 ${items.length} 条段，保留 outbox`)
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
  if (!await desiredEnabled()) return
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
