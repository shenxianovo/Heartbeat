import type { SegmentSnapshot } from './fold'
import { uuidv7 } from './ids'
import { detectBrowserAppIdentity } from './app-identity'
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
import { bindAttribution, readAttribution, sameSnapshot, snapshotRevision } from './protocol'
import type {
  BrowserActivationAttempt,
  BrowserPendingGap,
  BrowserProtocolSession,
  BrowserPublishAttempt,
} from './protocol'

const ATTRIBUTION_KEY = 'browserObservation'
const PREVIOUS_ATTRIBUTION_KEY = 'browserFactAttribution'
const QUEUE_KEY = 'pendingObservationFacts'
const PREVIOUS_QUEUE_KEY = 'pendingSegments'
const BACKOFF_KEY = 'backoff'
const HUB_PORT_KEY = 'hubPort'
const PROTOCOL_SESSION_KEY = 'collectorProtocolSession'
const PROTOCOL_ACTIVATION_ATTEMPT_KEY = 'collectorProtocolActivationAttempt'
const PROTOCOL_PUBLISH_ATTEMPT_KEY = 'collectorProtocolPublishAttempt'
const FLUSH_PERIOD_KEY = 'browserCollectorFlushPeriodMs'
const DEAD_LETTER_KEY = 'browserObservationDeadLetters'
const PREVIOUS_DEAD_LETTER_KEY = 'browserCollectorDeadLetters'
const PENDING_GAP_KEY = 'browserCollectorPendingGap'
const DESIRED_ENABLED_KEY = 'browserCollectorDesiredEnabled'
const DELIVERY_POLICY_KEY = 'browserCollectorDeliveryPolicy'
const EXTERNAL_HOST_IDENTITY_KEY = 'browserCollectorExternalHostIdentity'

type PersistedSegmentSnapshot = Omit<SegmentSnapshot, 'isFinal' | 'activityKey'> & {
  activityKey?: string
  identityKey?: string
  appName?: unknown
  isFinal?: boolean
}

/** Production storage adapter；所有 Chrome key 与旧布局迁移都停在这个内部 seam。 */
export class ChromeBrowserDeliveryStore implements BrowserDeliveryStore {
  private sessionStarted = false


  async loadDurable(): Promise<BrowserDeliveryDurableState> {
    const [local, transient] = await Promise.all([
      chrome.storage.local.get([
        QUEUE_KEY, ATTRIBUTION_KEY, PREVIOUS_QUEUE_KEY, PREVIOUS_ATTRIBUTION_KEY, PREVIOUS_DEAD_LETTER_KEY,
        PENDING_GAP_KEY,
        DEAD_LETTER_KEY,
        DELIVERY_POLICY_KEY,
      ]),
      chrome.storage.session.get([DESIRED_ENABLED_KEY, FLUSH_PERIOD_KEY]),
    ])
    const defaults = emptyBrowserDeliveryDurableState()
    const queue = mergeQueuedSnapshots(local[PREVIOUS_QUEUE_KEY], local[QUEUE_KEY])
    const rawGaps = local[PENDING_GAP_KEY]
    const policy = normalizePolicy(
      local[DELIVERY_POLICY_KEY],
      transient[DESIRED_ENABLED_KEY],
      transient[FLUSH_PERIOD_KEY],
    )
    const pendingGaps = normalizePendingGaps(rawGaps)
    if (pendingGaps.migrated) {
      await chrome.storage.local.set({ [PENDING_GAP_KEY]: pendingGaps.value })
    }
    const state: BrowserDeliveryDurableState = {
      queue,
      attribution: readAttribution(local[ATTRIBUTION_KEY] ?? local[PREVIOUS_ATTRIBUTION_KEY]),
      pendingGaps: pendingGaps.value,
      deadLetters: Object.values(normalizeQueuedSnapshots(Object.fromEntries(
        [...(Array.isArray(local[PREVIOUS_DEAD_LETTER_KEY]) ? local[PREVIOUS_DEAD_LETTER_KEY] as PersistedSegmentSnapshot[] : []),
          ...(Array.isArray(local[DEAD_LETTER_KEY]) ? local[DEAD_LETTER_KEY] as PersistedSegmentSnapshot[] : defaults.deadLetters)]
          .map(fact => [fact.id, fact])))),
      policy,
    }
    if ([PREVIOUS_QUEUE_KEY, PREVIOUS_ATTRIBUTION_KEY, PREVIOUS_DEAD_LETTER_KEY].some(key => local[key] !== undefined)) {
      await this.saveDurable(state)
      await chrome.storage.local.remove([PREVIOUS_QUEUE_KEY, PREVIOUS_ATTRIBUTION_KEY, PREVIOUS_DEAD_LETTER_KEY])
    }
    return state
  }

  async saveDurable(state: BrowserDeliveryDurableState): Promise<void> {
    await chrome.storage.local.set({
      [QUEUE_KEY]: state.queue,
      ...(state.attribution === undefined ? {} : { [ATTRIBUTION_KEY]: state.attribution }),
      [PENDING_GAP_KEY]: state.pendingGaps,
      [DEAD_LETTER_KEY]: state.deadLetters,
      [DELIVERY_POLICY_KEY]: state.policy,
    })
  }

  async loadSession(): Promise<BrowserDeliverySessionState> {
    if (!this.sessionStarted) {
      // A Service Worker owns one run. Unacknowledged Facts/Gaps remain in durable storage,
      // but the next worker negotiates a fresh Activation for the same External Host identity.
      await chrome.storage.session.remove([
        PROTOCOL_SESSION_KEY, PROTOCOL_ACTIVATION_ATTEMPT_KEY, PROTOCOL_PUBLISH_ATTEMPT_KEY,
      ])
      this.sessionStarted = true
    }
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
    this.sessionStarted = true
  }
}

function normalizePendingGaps(raw: unknown): { value: BrowserPendingGap[]; migrated: boolean } {
  // Current browser storage predates GapId. The removal threshold is the supported-Profile
  // inventory and offline/rollback window in docs/architecture/compatibility-debt.md.
  const gaps = (Array.isArray(raw) ? raw : raw === undefined ? [] : [raw]) as BrowserPendingGap[]
  let migrated = false
  const value = gaps.map((gap) => {
    if (typeof gap.gapId === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(gap.gapId)) {
      return gap
    }
    migrated = true
    return { ...gap, gapId: uuidv7() }
  })
  return { value, migrated }
}

export function createChromeBrowserDelivery(): BrowserDelivery {
  return createBrowserDelivery({
    store: new ChromeBrowserDeliveryStore(),
    hub: new LoopbackBrowserHubAdapter(),
    loadAppIdentityKey: async () => {
      const nav = navigator as Navigator & {
        userAgentData?: { brands?: { brand: string }[] }
        brave?: unknown
      }
      const platform = await chrome.runtime.getPlatformInfo()
      return detectBrowserAppIdentity({
        platform: platform.os,
        brands: nav.userAgentData?.brands?.map(item => item.brand),
        userAgent: nav.userAgent,
        hasBraveApi: nav.brave !== undefined,
      })
    },
    loadBasePort: async () => (await loadConfig()).port,
    loadExternalHostIdentity,
  })
}

export async function loadExternalHostIdentity(): Promise<string> {
  const stored = await chrome.storage.local.get(EXTERNAL_HOST_IDENTITY_KEY)
  const existing = stored[EXTERNAL_HOST_IDENTITY_KEY]
  if (typeof existing === 'string' && existing.length > 0) return existing
  const created = crypto.randomUUID()
  await chrome.storage.local.set({ [EXTERNAL_HOST_IDENTITY_KEY]: created })
  return created
}

function mergeQueuedSnapshots(previous: unknown, current: unknown): Record<string, SegmentSnapshot> {
  const merged = normalizeQueuedSnapshots(isRecord(previous) ? previous as Record<string, PersistedSegmentSnapshot> : {})
  const incoming = normalizeQueuedSnapshots(isRecord(current) ? current as Record<string, PersistedSegmentSnapshot> : {})
  for (const [id, fact] of Object.entries(incoming)) {
    const old = merged[id]
    if (!old || snapshotRevision(fact) > snapshotRevision(old)) merged[id] = fact
    else if (snapshotRevision(fact) === snapshotRevision(old)) {
      if (!sameSnapshot(bindAttribution(old, readAttribution(fact)), bindAttribution(fact, readAttribution(old))))
        throw new Error(`Conflicting cached revision for Fact ${id}; preserve both queues`)
      merged[id] = fact
    }
  }
  return merged
}

function normalizeQueuedSnapshots(
  stored: Record<string, PersistedSegmentSnapshot>,
): Record<string, SegmentSnapshot> {
  return Object.fromEntries(
    Object.entries(stored).map(([id, snapshot]) => [id, {
      id: snapshot.id,
      source: snapshot.source,
      activityKey: snapshot.activityKey ?? snapshot.identityKey!,
      ...readAttribution(snapshot),
      title: snapshot.title,
      startTime: snapshot.startTime,
      endTime: snapshot.endTime,
      isFinal: snapshot.isFinal === true,
      attributes: snapshot.attributes,
    }]),
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
