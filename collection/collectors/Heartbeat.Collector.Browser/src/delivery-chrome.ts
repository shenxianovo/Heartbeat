import type { SegmentSnapshot } from './fold'
import { loadConfig } from './config'
import { LoopbackBrowserHubAdapter } from './hub'
import {
  createBrowserDelivery,
  defaultBrowserDeliverySession,
  emptyBrowserDeliveryDurableState,
  type BrowserCollectionPolicy,
  type BrowserDelivery,
  type BrowserDeliveryDurableState,
  type BrowserDeliverySessionState,
  type BrowserDeliveryStore,
} from './delivery'
import type {
  BrowserActivationAttempt,
  BrowserPendingGap,
  BrowserProtocolSession,
  BrowserPublishAttempt,
} from './protocol'

const QUEUE_KEY = 'pendingSegments'
const BACKOFF_KEY = 'backoff'
const HUB_PORT_KEY = 'hubPort'
const PROTOCOL_SESSION_KEY = 'collectorProtocolSession'
const PROTOCOL_ACTIVATION_ATTEMPT_KEY = 'collectorProtocolActivationAttempt'
const PROTOCOL_PUBLISH_ATTEMPT_KEY = 'collectorProtocolPublishAttempt'
const FLUSH_PERIOD_KEY = 'browserCollectorFlushPeriodMs'
const DEAD_LETTER_KEY = 'browserCollectorDeadLetters'
const PENDING_GAP_KEY = 'browserCollectorPendingGap'
const DESIRED_ENABLED_KEY = 'browserCollectorDesiredEnabled'
const DELIVERY_POLICY_KEY = 'browserCollectorDeliveryPolicy'

type PersistedSegmentSnapshot = Omit<SegmentSnapshot, 'isFinal'> & {
  appName?: unknown
  isFinal?: boolean
}

/** Production storage adapter；所有 Chrome key 与旧布局迁移都停在这个内部 seam。 */
export class ChromeBrowserDeliveryStore implements BrowserDeliveryStore {
  constructor(private readonly currentAppHint: string | undefined) {}

  async loadDurable(): Promise<BrowserDeliveryDurableState> {
    const [local, transient] = await Promise.all([
      chrome.storage.local.get([
        QUEUE_KEY,
        PENDING_GAP_KEY,
        DEAD_LETTER_KEY,
        DELIVERY_POLICY_KEY,
      ]),
      chrome.storage.session.get([DESIRED_ENABLED_KEY, FLUSH_PERIOD_KEY]),
    ])
    const defaults = emptyBrowserDeliveryDurableState()
    const rawQueue = isRecord(local[QUEUE_KEY])
      ? local[QUEUE_KEY] as Record<string, PersistedSegmentSnapshot>
      : {}
    const rawGaps = local[PENDING_GAP_KEY]
    const policy = normalizePolicy(
      local[DELIVERY_POLICY_KEY],
      transient[DESIRED_ENABLED_KEY],
      transient[FLUSH_PERIOD_KEY],
    )
    return {
      queue: normalizeQueuedSnapshots(rawQueue, this.currentAppHint),
      pendingGaps: Array.isArray(rawGaps)
        ? rawGaps as BrowserPendingGap[]
        : rawGaps === undefined ? [] : [rawGaps as BrowserPendingGap],
      deadLetters: Array.isArray(local[DEAD_LETTER_KEY])
        ? local[DEAD_LETTER_KEY] as SegmentSnapshot[]
        : defaults.deadLetters,
      policy,
    }
  }

  async saveDurable(state: BrowserDeliveryDurableState): Promise<void> {
    await chrome.storage.local.set({
      [QUEUE_KEY]: state.queue,
      [PENDING_GAP_KEY]: state.pendingGaps,
      [DEAD_LETTER_KEY]: state.deadLetters,
      [DELIVERY_POLICY_KEY]: state.policy,
    })
  }

  async loadSession(): Promise<BrowserDeliverySessionState> {
    const got = await chrome.storage.session.get([
      BACKOFF_KEY,
      HUB_PORT_KEY,
      PROTOCOL_SESSION_KEY,
      PROTOCOL_ACTIVATION_ATTEMPT_KEY,
      PROTOCOL_PUBLISH_ATTEMPT_KEY,
    ])
    const defaults = defaultBrowserDeliverySession()
    return {
      backoff: normalizeBackoff(got[BACKOFF_KEY]) ?? defaults.backoff,
      ...(positivePort(got[HUB_PORT_KEY]) === undefined ? {} : { hubPort: Number(got[HUB_PORT_KEY]) }),
      ...(got[PROTOCOL_SESSION_KEY] === undefined
        ? {} : { protocolSession: got[PROTOCOL_SESSION_KEY] as BrowserProtocolSession }),
      ...(got[PROTOCOL_ACTIVATION_ATTEMPT_KEY] === undefined
        ? {} : { activationAttempt: got[PROTOCOL_ACTIVATION_ATTEMPT_KEY] as BrowserActivationAttempt }),
      ...(got[PROTOCOL_PUBLISH_ATTEMPT_KEY] === undefined
        ? {} : { publishAttempt: got[PROTOCOL_PUBLISH_ATTEMPT_KEY] as BrowserPublishAttempt }),
    }
  }

  async saveSession(state: BrowserDeliverySessionState): Promise<void> {
    await chrome.storage.session.set({
      [BACKOFF_KEY]: state.backoff,
      ...(state.hubPort === undefined ? {} : { [HUB_PORT_KEY]: state.hubPort }),
      ...(state.protocolSession === undefined ? {} : { [PROTOCOL_SESSION_KEY]: state.protocolSession }),
      ...(state.activationAttempt === undefined
        ? {} : { [PROTOCOL_ACTIVATION_ATTEMPT_KEY]: state.activationAttempt }),
      ...(state.publishAttempt === undefined
        ? {} : { [PROTOCOL_PUBLISH_ATTEMPT_KEY]: state.publishAttempt }),
    })
    const remove = [
      ...(state.hubPort === undefined ? [HUB_PORT_KEY] : []),
      ...(state.protocolSession === undefined ? [PROTOCOL_SESSION_KEY] : []),
      ...(state.activationAttempt === undefined ? [PROTOCOL_ACTIVATION_ATTEMPT_KEY] : []),
      ...(state.publishAttempt === undefined ? [PROTOCOL_PUBLISH_ATTEMPT_KEY] : []),
    ]
    if (remove.length > 0) await chrome.storage.session.remove(remove)
  }
}

export function createChromeBrowserDelivery(appHint: string | undefined): BrowserDelivery {
  return createBrowserDelivery({
    store: new ChromeBrowserDeliveryStore(appHint),
    hub: new LoopbackBrowserHubAdapter(),
    appHint,
    loadBasePort: async () => (await loadConfig()).port,
  })
}

function normalizeQueuedSnapshots(
  stored: Record<string, PersistedSegmentSnapshot>,
  currentAppHint: string | undefined,
): Record<string, SegmentSnapshot> {
  return Object.fromEntries(
    Object.entries(stored).map(([id, { appName: _legacyAppName, ...snapshot }]) => [
      id,
      {
        ...snapshot,
        isFinal: snapshot.isFinal === true,
        ...(snapshot.appHint === undefined && currentAppHint !== undefined
          ? { appHint: currentAppHint }
          : {}),
      },
    ]),
  )
}

function normalizePolicy(
  durable: unknown,
  legacyEnabled: unknown,
  legacyFlushPeriod: unknown,
): BrowserCollectionPolicy {
  if (isRecord(durable)) {
    const flushPeriodMilliseconds = positiveFlushPeriod(durable.flushPeriodMilliseconds)
    if (typeof durable.enabled === 'boolean' && flushPeriodMilliseconds !== undefined) {
      return { enabled: durable.enabled, flushPeriodMilliseconds }
    }
  }
  return {
    enabled: legacyEnabled !== false,
    flushPeriodMilliseconds: positiveFlushPeriod(legacyFlushPeriod) ?? 30_000,
  }
}

function normalizeBackoff(value: unknown): BrowserDeliverySessionState['backoff'] | undefined {
  if (!isRecord(value)) return undefined
  const fails = Number(value.fails)
  const nextAttemptAt = Number(value.nextAttemptAt)
  return Number.isSafeInteger(fails) && fails >= 0 &&
    Number.isSafeInteger(nextAttemptAt) && nextAttemptAt >= 0
    ? { fails, nextAttemptAt }
    : undefined
}

function positiveFlushPeriod(value: unknown): number | undefined {
  const number = Number(value)
  return Number.isSafeInteger(number) && number >= 30_000 ? number : undefined
}

function positivePort(value: unknown): number | undefined {
  const number = Number(value)
  return Number.isSafeInteger(number) && number > 0 && number <= 65_535 ? number : undefined
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
