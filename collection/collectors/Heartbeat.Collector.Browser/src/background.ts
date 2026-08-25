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
import { enqueueBounded, normalizeQueuedSnapshots } from './queue'
import {
  uploadWithBrowserProtocol,
  snapshotRevision,
  type BrowserActivationAttempt,
  type BrowserPendingGap,
  type BrowserPublishAttempt,
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
const PROTOCOL_ACTIVATION_ATTEMPT_KEY = 'collectorProtocolActivationAttempt'
const PROTOCOL_PUBLISH_ATTEMPT_KEY = 'collectorProtocolPublishAttempt'
const FLUSH_PERIOD_KEY = 'browserCollectorFlushPeriodMs'
const DEAD_LETTER_KEY = 'browserCollectorDeadLetters'
const MAX_DEAD_LETTERS = 100
const PENDING_GAP_KEY = 'browserCollectorPendingGap'
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

async function loadPendingGaps(): Promise<BrowserPendingGap[]> {
  const got = await chrome.storage.local.get(PENDING_GAP_KEY)
  const stored = got[PENDING_GAP_KEY]
  if (Array.isArray(stored)) return stored as BrowserPendingGap[]
  return stored === undefined ? [] : [stored as BrowserPendingGap]
}

async function savePendingGaps(gaps: BrowserPendingGap[]): Promise<void> {
  if (gaps.length === 0) await chrome.storage.local.remove(PENDING_GAP_KEY)
  else await chrome.storage.local.set({ [PENDING_GAP_KEY]: gaps })
}

async function recordBufferGap(snapshots: SegmentSnapshot[]): Promise<void> {
  if (snapshots.length === 0) return
  const starts = snapshots.map((snapshot) => snapshot.startTime)
  const ends = snapshots.map((snapshot) => snapshot.endTime)
  const gap: BrowserPendingGap = {
    start: starts.sort()[0],
    end: ends.sort().at(-1)!,
    reason: 'buffer_overflow',
    estimatedFactsLost: snapshots.length,
  }
  await savePendingGaps([...(await loadPendingGaps()), gap])
}

async function persistFirstGapAttempt(gap: BrowserPendingGap): Promise<void> {
  const gaps = await loadPendingGaps()
  if (gaps.length === 0) return
  gaps[0] = gap
  await savePendingGaps(gaps)
}

async function appendDeadLetters(snapshots: SegmentSnapshot[]): Promise<void> {
  if (snapshots.length === 0) return
  const got = await chrome.storage.local.get(DEAD_LETTER_KEY)
  const existing = Array.isArray(got[DEAD_LETTER_KEY])
    ? got[DEAD_LETTER_KEY] as SegmentSnapshot[]
    : []
  await chrome.storage.local.set({
    [DEAD_LETTER_KEY]: [...existing, ...snapshots].slice(-MAX_DEAD_LETTERS),
  })
  console.warn(`[heartbeat] ${snapshots.length} 条 Fact 被 Hub 永久拒绝，已移入诊断 dead-letter`)
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

async function loadProtocolActivationAttempt(): Promise<BrowserActivationAttempt | undefined> {
  const got = await chrome.storage.session.get(PROTOCOL_ACTIVATION_ATTEMPT_KEY)
  return got[PROTOCOL_ACTIVATION_ATTEMPT_KEY] as BrowserActivationAttempt | undefined
}

async function saveProtocolActivationAttempt(attempt: BrowserActivationAttempt | undefined): Promise<void> {
  if (attempt === undefined) await chrome.storage.session.remove(PROTOCOL_ACTIVATION_ATTEMPT_KEY)
  else await chrome.storage.session.set({ [PROTOCOL_ACTIVATION_ATTEMPT_KEY]: attempt })
}

async function loadProtocolPublishAttempt(): Promise<BrowserPublishAttempt | undefined> {
  const got = await chrome.storage.session.get(PROTOCOL_PUBLISH_ATTEMPT_KEY)
  return got[PROTOCOL_PUBLISH_ATTEMPT_KEY] as BrowserPublishAttempt | undefined
}

async function saveProtocolPublishAttempt(attempt: BrowserPublishAttempt | undefined): Promise<void> {
  if (attempt === undefined) await chrome.storage.session.remove(PROTOCOL_PUBLISH_ATTEMPT_KEY)
  else await chrome.storage.session.set({ [PROTOCOL_PUBLISH_ATTEMPT_KEY]: attempt })
}

async function desiredEnabled(): Promise<boolean> {
  const got = await chrome.storage.session.get(DESIRED_ENABLED_KEY)
  return got[DESIRED_ENABLED_KEY] !== false
}

async function saveDesiredEnabled(enabled: boolean): Promise<void> {
  await chrome.storage.session.set({ [DESIRED_ENABLED_KEY]: enabled })
}

async function desiredFlushPeriodMilliseconds(): Promise<number> {
  const got = await chrome.storage.session.get(FLUSH_PERIOD_KEY)
  const value = Number(got[FLUSH_PERIOD_KEY])
  return Number.isSafeInteger(value) && value >= 30_000 ? value : FLUSH_PERIOD_MS
}

async function applyProtocolSpec(spec: {
  enabled: boolean
  flushPeriodMilliseconds: number
}): Promise<void> {
  await saveDesiredEnabled(spec.enabled)
  await chrome.storage.session.set({ [FLUSH_PERIOD_KEY]: spec.flushPeriodMilliseconds })
  chrome.alarms.create(ALARM_NAME, {
    periodInMinutes: spec.flushPeriodMilliseconds / 60_000,
  })
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
  const { queue, overflow } = enqueueBounded(await loadQueue(), snapshots)
  try {
    await saveQueue(queue)
  } catch (error) {
    console.warn('[heartbeat] outbox 写入失败，记录 Stream Gap', error)
    await recordBufferGap(snapshots)
    return
  }
  await recordBufferGap(overflow)
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

  // 礼貌层停用（ADR-026 §4）：每轮 flush 拉一次 hub 侧配置——此调用同时是注册
  // （首次触达即"已安装"）与 flushPeriodMs 自报。enabled:false 时保留 outbox、不上报，
  // 免去注定被 403 的无效 POST；拉取失败（hub 不在/端口漂移）保守视为未停用。
  const collectorConfig = await fetchCollectorConfig(
    compatiblePort,
    SOURCE,
    await desiredFlushPeriodMilliseconds(),
  )
  if (collectorConfig?.enabled === false) {
    await applyDesiredEnabled(false)
    return
  }
  if (collectorConfig?.enabled === true) await applyDesiredEnabled(true)

  const queue = await loadQueue()
  const items = Object.values(queue)

  // outbox 为空也要完成或续租 Activation：新 Hub 由已验证 Package 注册 typed-payload
  // 声明，并让浏览器退出后能通过租约如实结束会话。
  const protocolResult = await uploadWithBrowserProtocol(
    compatiblePort,
    deps.appHint,
    items,
    await loadProtocolSession(),
    await loadProtocolActivationAttempt(),
    await loadProtocolPublishAttempt(),
    saveProtocolActivationAttempt,
    saveProtocolPublishAttempt,
    applyProtocolSpec,
    (await loadPendingGaps())[0],
    persistFirstGapAttempt,
  )

  if ((protocolResult.kind === 'acked' || protocolResult.kind === 'unavailable') &&
      protocolResult.gapAcknowledged === true) {
    const gaps = await loadPendingGaps()
    await savePendingGaps(gaps.slice(1))
  }

  if (protocolResult.kind === 'acked') {
    const latestQueue = await loadQueue()
    const rejected = Object.entries(latestQueue)
      .filter(([id, snapshot]) =>
        protocolResult.rejectedRevisions[id] === snapshotRevision(snapshot))
      .map(([, snapshot]) => snapshot)
    await appendDeadLetters(rejected)
    const remaining = Object.fromEntries(
      Object.entries(latestQueue).filter(([id, snapshot]) =>
        protocolResult.acknowledgedRevisions[id] !== snapshotRevision(snapshot) &&
        protocolResult.rejectedRevisions[id] !== snapshotRevision(snapshot),
      ),
    )
    await saveQueue(remaining)
    await saveProtocolSession(protocolResult.session)
    await saveProtocolActivationAttempt(undefined)
    await saveProtocolPublishAttempt(protocolResult.nextPublishAttempt)
    if (protocolResult.retryAfterMilliseconds !== undefined) {
      await saveBackoff({
        fails: 0,
        nextAttemptAt: now + protocolResult.retryAfterMilliseconds,
      })
    } else if (backoff.fails > 0) await saveBackoff(noBackoff)
    return
  }
  if (protocolResult.kind === 'disabled') {
    await saveProtocolActivationAttempt(undefined)
    await saveProtocolPublishAttempt(undefined)
    await applyDesiredEnabled(false)
    return
  }
  if (protocolResult.kind === 'unavailable') {
    if (protocolResult.activationAttempt !== undefined) {
      await saveProtocolActivationAttempt(protocolResult.activationAttempt)
    } else {
      await saveProtocolActivationAttempt(undefined)
    }
    if (protocolResult.publishAttempt !== undefined) {
      await saveProtocolPublishAttempt(protocolResult.publishAttempt)
      if (protocolResult.session !== undefined) await saveProtocolSession(protocolResult.session)
    } else {
      await saveProtocolPublishAttempt(undefined)
      await saveProtocolSession(undefined)
    }
    await saveBackoff(backoffAfterFailure(backoff, now))
    return
  }

  // 旧缓存（非 UUIDv7）或旧 hub 明确要求 legacy adapter 时，才沿旧路由上报声明。
  // 这样不会抢先以同版本 legacy 声明遮蔽新 Hub 从 Package 注册的 typed-payload 声明。
  const acked = await chrome.storage.local.get(DECLARATION_ACK_KEY)
  await saveProtocolSession(undefined)
  await saveProtocolActivationAttempt(undefined)
  await saveProtocolPublishAttempt(undefined)
  if (acked[DECLARATION_ACK_KEY] !== DECLARATION.version) {
    if (await postDeclaration(compatiblePort, DECLARATION))
      await chrome.storage.local.set({ [DECLARATION_ACK_KEY]: DECLARATION.version })
  }
  if (items.length === 0) return

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
