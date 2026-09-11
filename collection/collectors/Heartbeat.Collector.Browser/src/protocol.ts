import type { BrowserAttribution, SegmentSnapshot } from './fold'
import { uuidv7 } from './ids'

import { protocolFetch } from './connection'

interface BrowserPackageReference {
  packageId: string
  packageVersion: string
  packageContentHash: string
  artifactId: string
  artifactHash: string
}

async function browserPackageReference(): Promise<BrowserPackageReference> {
  const response = await fetch(chrome.runtime.getURL('collector-artifact-ref.json'))
  if (!response.ok) throw new Error('Browser Package metadata is unavailable')
  const metadata = await response.json() as BrowserPackageReference
  if (metadata?.packageId !== 'heartbeat.collector.browser' ||
      metadata.artifactId !== 'browser.extension' ||
      typeof metadata.packageVersion !== 'string' || !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(metadata.packageVersion) ||
      ![metadata.packageContentHash, metadata.artifactHash].every(value =>
        typeof value === 'string' && /^sha256:[0-9a-f]{64}$/.test(value))) {
    throw new Error('Browser Package metadata is invalid')
  }
  return metadata
}

export interface BrowserProtocolSession {
  attribution?: BrowserAttribution
  port: number
  activationId: string
  leaseToken: string
  streamId: string
  specRevision: number
  expiresAt: string
  limits: ProtocolLimits
  flushPeriodMilliseconds: number
}

export interface BrowserActivationAttempt {
  helloMessageId: string
  initializedMessageId: string
  streamsMessageId: string
  readyMessageId: string
}

export interface BrowserPublishAttempt {
  activationId: string
  messageId: string
  snapshots: SegmentSnapshot[]
}

export interface BrowserPendingGap {
  gapId: string
  messageId?: string
  activationId?: string
  start: string
  end: string
  reason: 'buffer_overflow'
  estimatedFactsLost: number
}

interface ProtocolLimits {
  maxFactsPerBatch: number
  maxBatchBytes: number
}

const DEFAULT_LIMITS: ProtocolLimits = {
  maxFactsPerBatch: 500,
  maxBatchBytes: 1_048_576,
}

export type ProtocolUploadResult =
  | {
      kind: 'acked'
      settledSnapshots?: SegmentSnapshot[]
      acknowledgedIds: string[]
      acknowledgedRevisions: Record<string, number>
      rejectedRevisions: Record<string, number>
      retryAfterMilliseconds?: number
      nextPublishAttempt?: BrowserPublishAttempt
      gapAcknowledged?: boolean
      session: BrowserProtocolSession
    }
  | { kind: 'disabled' }
  | {
      kind: 'unavailable'
      activationAttempt?: BrowserActivationAttempt
      publishAttempt?: BrowserPublishAttempt
      session?: BrowserProtocolSession
      gapAcknowledged?: boolean
    }

interface HelloResponse {
  activationId: string
  selectedProtocolMajor: number
  selectedCapabilities: Record<string, number>
}

interface InitializeResponse {
  // Pre-Target initialization protocol; only this adapter reads the legacy Subject.
  instance: { subject: { subjectId: string; kind: string } }

  spec: {
    revision: number
    config: { value: { enabled?: boolean; flushPeriodMs?: number } }
  }
  limits: ProtocolLimits
}

interface StreamsOpenedResponse {
  streams: Record<string, { streamId: string }>
}

interface ReadyResponse {
  lease: { token: string; expiresAt: string }
}

interface AckResponse {
  results: {
    index: number
    status: string
    retryAfterMs?: number
    error?: { code?: string; message?: string }
  }[]
}

interface ProtocolMessage<T> {
  protocol: string
  type: string
  messageId: string
  activationId?: string
  replyTo?: string
  body: T
}

const acknowledgedStatuses = new Set(['committed', 'duplicate', 'superseded'])

export function isUuidV7(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value)
}

export function snapshotRevision(snapshot: SegmentSnapshot): number {
  const revision = snapshot.revision ?? Date.parse(snapshot.endTime)
  return Number.isSafeInteger(revision) && revision > 0 ? revision : 1
}

export function toProtocolFact(snapshot: SegmentSnapshot, streamId: string) {
  if (!isUuidV7(snapshot.id) || (snapshot.kind !== undefined &&
      (!Number.isSafeInteger(snapshot.revision) || snapshot.revision! <= 0))) return null
  const { id: _id, source: _source, startTime: _start, endTime: _end, isFinal: _final,
    kind: _kind, revision: _revision, collectorId: _collector, foi: _foi, relations: _relations,
    streamId: _stream, observedAt: _observed, aspect: _aspect, result, ...flatResult } = snapshot
  return {
    ...(snapshot.kind === undefined ? { streamId: snapshot.streamId ?? streamId } : { kind: snapshot.kind, source: snapshot.source }),
    factId: snapshot.id,
    revision: snapshotRevision(snapshot),
    aspect: snapshot.aspect === undefined ? 'selected-page' : snapshot.aspect,
    collectorId: snapshot.collectorId,
    foi: snapshot.foi,
    relations: snapshot.relations,
    observedAt: snapshot.observedAt ?? null,
    time: {
      start: snapshot.startTime,
      end: snapshot.endTime,
      isFinal: snapshot.isFinal,
    },
    payload: result ?? flatResult,
  }
}

export function acknowledgedSnapshotIds(
  snapshots: SegmentSnapshot[],
  acknowledgement: AckResponse,
): string[] {
  return acknowledgement.results
    .filter((result) =>
      Number.isInteger(result.index) &&
      result.index >= 0 &&
      result.index < snapshots.length &&
      acknowledgedStatuses.has(result.status),
    )
    .map((result) => snapshots[result.index].id)
}

export async function openBrowserProtocolSession(
  port: number,
  appIdentityKey: string,
  externalHostIdentity: string,
  attempt: BrowserActivationAttempt,
  applySpec?: (spec: { enabled: boolean; flushPeriodMilliseconds: number }) => Promise<void>,
): Promise<BrowserProtocolSession | 'disabled' | 'rejected' | null> {
  try {
    const hello = await protocolFetch(port, '/hello', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(message(
        'heartbeat.collector.bootstrap/1',
        'activation.hello',
        attempt.helloMessageId,
        undefined,
        {
        ...await browserPackageReference(),
        protocolMajors: [1],
        supportedCapabilities: {
          'facts.observation': [2],
          'facts.aspect': [1],
          'facts.segment': [1],
          'diagnostics.stream-gap': [1],
        },
        appIdentityKey,
        externalHostIdentity,
      })),
    })
    if (!hello.ok) return 'rejected'
    const acceptedMessage = (await hello.json()) as ProtocolMessage<HelloResponse>
    if (!isCorrelatedResponse(
      acceptedMessage,
      'heartbeat.collector.bootstrap/1',
      'activation.accepted',
      undefined,
      attempt.helloMessageId,
    ) || !isUuidV7(acceptedMessage.body.activationId) ||
      acceptedMessage.body.selectedProtocolMajor !== 1 ||
      acceptedMessage.body.selectedCapabilities?.['facts.observation'] !== 2 ||
      acceptedMessage.body.selectedCapabilities?.['facts.aspect'] !== 1 ||
      acceptedMessage.body.selectedCapabilities?.['facts.segment'] !== 1 ||
      acceptedMessage.body.selectedCapabilities?.['diagnostics.stream-gap'] !== 1)
      return 'rejected'
    const accepted = acceptedMessage.body
    const initialize = await protocolFetch(
      port, `/${accepted.activationId}/initialize`,
      { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' },
    )
    if (!initialize.ok) return 'rejected'
    const initializeMessage = (await initialize.json()) as ProtocolMessage<InitializeResponse>
    if (!isCorrelatedResponse(
      initializeMessage,
      'heartbeat.collector/1',
      'activation.initialize',
      accepted.activationId,
      undefined,
    )) return 'rejected'
    const initialized = initializeMessage.body
    const deviceReference = initialized.instance?.subject?.subjectId
    if (initialized.instance?.subject?.kind !== 'machine' || !isUuid(deviceReference) || !isUuid(externalHostIdentity)) return 'rejected'
    const attribution = browserAttribution(externalHostIdentity.toLowerCase(), deviceReference.toLowerCase(), appIdentityKey)
    if (initialized.spec.config.value.enabled === false) return 'disabled'
    const flushPeriodMilliseconds = positiveInteger(initialized.spec.config.value.flushPeriodMs)
    if (flushPeriodMilliseconds === undefined || flushPeriodMilliseconds < 30_000) return 'rejected'
    if (positiveInteger(initialized.limits?.maxFactsPerBatch) === undefined ||
      positiveInteger(initialized.limits?.maxBatchBytes) === undefined)
      return 'rejected'
    await applySpec?.({ enabled: true, flushPeriodMilliseconds })

    const initializedAck = await protocolFetch(
      port, `/${accepted.activationId}/initialized`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message(
          'heartbeat.collector/1',
          'activation.initialized',
          attempt.initializedMessageId,
          accepted.activationId,
          { appliedSpecRevision: initialized.spec.revision },
          initializeMessage.messageId,
        )),
      },
    )
    if (!initializedAck.ok) return 'rejected'

    const streams = await protocolFetch(
      port, `/${accepted.activationId}/streams`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message(
          'heartbeat.collector/1',
          'streams.open',
          attempt.streamsMessageId,
          accepted.activationId,
          {
          specRevision: initialized.spec.revision,
          bindings: [{ bindingId: 'tabs', outputId: 'activeTab', dimensions: {} }],
        })),
      },
    )
    if (!streams.ok) return 'rejected'
    const openedMessage = (await streams.json()) as ProtocolMessage<StreamsOpenedResponse>
    if (!isCorrelatedResponse(
      openedMessage,
      'heartbeat.collector/1',
      'streams.opened',
      accepted.activationId,
      attempt.streamsMessageId,
    )) return 'rejected'
    const opened = openedMessage.body
    const stream = opened.streams.tabs
    if (!stream?.streamId) return 'rejected'

    const ready = await protocolFetch(
      port, `/${accepted.activationId}/ready`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message(
          'heartbeat.collector/1',
          'activation.ready',
          attempt.readyMessageId,
          accepted.activationId,
          {
          appliedSpecRevision: initialized.spec.revision,
        })),
      },
    )
    if (!ready.ok) return 'rejected'
    const readyMessage = (await ready.json()) as ProtocolMessage<ReadyResponse>
    if (!isCorrelatedResponse(
      readyMessage,
      'heartbeat.collector/1',
      'activation.readyAck',
      accepted.activationId,
      attempt.readyMessageId,
    )) return 'rejected'
    const readyAcknowledgement = readyMessage.body
    if (!readyAcknowledgement.lease?.token) return null
    return {
      port,
      activationId: accepted.activationId,
      leaseToken: readyAcknowledgement.lease.token,
      streamId: stream.streamId,
      specRevision: initialized.spec.revision,
      expiresAt: readyAcknowledgement.lease.expiresAt,
      limits: normalizeLimits(initialized.limits),
      flushPeriodMilliseconds,
      attribution,
    }
  } catch {
    return null
  }
}

export async function renewBrowserProtocolSession(
  session: BrowserProtocolSession,
): Promise<BrowserProtocolSession | null> {
  try {
    const response = await protocolFetch(
      session.port, `/${session.activationId}/renew`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ leaseToken: session.leaseToken }),
      },
    )
    if (!response.ok) return null
    const lease = (await response.json()) as { token?: string; expiresAt?: string }
    return lease.token === session.leaseToken && typeof lease.expiresAt === 'string'
      ? { ...session, expiresAt: lease.expiresAt }
      : null
  } catch {
    return null
  }
}

export async function publishBrowserFacts(
  session: BrowserProtocolSession,
  snapshots: SegmentSnapshot[],
  previousAttempt?: BrowserPublishAttempt,
  persistAttempt?: (attempt: BrowserPublishAttempt) => Promise<void>,
): Promise<ProtocolUploadResult> {
  const limits = normalizeLimits(session.limits)
  const maxFacts = Math.max(1, Math.min(limits.maxFactsPerBatch, 500))
  const reusableAttempt = previousAttempt?.activationId === session.activationId
    ? previousAttempt
    : undefined
  const bound = snapshots.map(snapshot => bindAttribution(snapshot, session.attribution))
  if (bound.some(snapshot => !snapshot.collectorId || !snapshot.foi || !snapshot.relations)) return { kind: 'unavailable' }
  const batch = reusableAttempt?.snapshots ?? takeBatchWithinByteLimit(bound, session, maxFacts)
  if (snapshots.length > 0 && batch.length === 0) return { kind: 'unavailable' }
  const facts = batch.map((snapshot) => toProtocolFact(snapshot, session.streamId))
  if (facts.some((fact) => fact === null)) return { kind: 'unavailable', session }
  if (facts.length === 0) {
    return {
      kind: 'acked',
      acknowledgedIds: [],
      acknowledgedRevisions: {},
      rejectedRevisions: {},
      session,
    }
  }
  const attempt = reusableAttempt ?? { activationId: session.activationId, messageId: uuidv7(), snapshots: batch }
  await persistAttempt?.(attempt)
  try {
    const response = await protocolFetch(
      session.port, `/${session.activationId}/facts`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message(
          'heartbeat.collector/1',
          'facts.publish',
          attempt.messageId,
          session.activationId,
          {
          leaseToken: session.leaseToken,
          facts,
        })),
      },
    )
    if (response.status === 403) return { kind: 'disabled' }
    if (!response.ok) return { kind: 'unavailable' }
    const acknowledgementMessage = (await response.json()) as ProtocolMessage<AckResponse>
    if (!isCorrelatedResponse(
      acknowledgementMessage,
      'heartbeat.collector/1',
      'facts.ack',
      session.activationId,
      attempt.messageId,
    ) || !hasCompleteFactResults(acknowledgementMessage.body, batch.length)) {
      throw new Error('facts.ack is malformed or does not match the publish attempt')
    }
    const acknowledgement = acknowledgementMessage.body
    const acknowledgedIds = acknowledgedSnapshotIds(batch, acknowledgement)
    const rejected = acknowledgement.results.filter((result) =>
      Number.isInteger(result.index) && result.index >= 0 && result.index < batch.length &&
      result.status === 'rejected')
    const retryResults = acknowledgement.results.filter((result) => result.status === 'retry')
    const retries = retryResults.map((result) => positiveInteger(result.retryAfterMs) ?? 1_000)
    const nextPublishAttempt = retryResults.length === 0
      ? undefined
      : {
          activationId: session.activationId,
          messageId: uuidv7(),
          snapshots: retryResults.map((result) => batch[result.index]),
        }
    if (nextPublishAttempt !== undefined) await persistAttempt?.(nextPublishAttempt)
    return {
      kind: 'acked',
      acknowledgedIds,
      settledSnapshots: batch,
      acknowledgedRevisions: Object.fromEntries(
        acknowledgedIds.map((id) => [
          id,
          snapshotRevision(batch.find((snapshot) => snapshot.id === id)!),
        ]),
      ),
      rejectedRevisions: Object.fromEntries(rejected.map((result) => [
        batch[result.index].id,
        snapshotRevision(batch[result.index]),
      ])),
      ...(retries.length === 0 ? {} : { retryAfterMilliseconds: Math.max(...retries) }),
      ...(nextPublishAttempt === undefined ? {} : { nextPublishAttempt }),
      session,
    }
  } catch {
    return { kind: 'unavailable', publishAttempt: attempt, session }
  }
}

export async function uploadWithBrowserProtocol(
  port: number,
  appIdentityKey: string | undefined,
  externalHostIdentity: string,
  snapshots: SegmentSnapshot[],
  previousSession?: BrowserProtocolSession,
  previousActivationAttempt?: BrowserActivationAttempt,
  previousPublishAttempt?: BrowserPublishAttempt,
  persistActivationAttempt?: (attempt: BrowserActivationAttempt) => Promise<void>,
  persistPublishAttempt?: (attempt: BrowserPublishAttempt) => Promise<void>,
  applySpec?: (spec: { enabled: boolean; flushPeriodMilliseconds: number }) => Promise<void>,
  pendingGap?: BrowserPendingGap,
  persistGapAttempt?: (gap: BrowserPendingGap) => Promise<void>,
  applyAttribution?: (attribution: BrowserAttribution) => Promise<void>,
  recoveryFactIds?: string[],
  applyRecoveredFacts?: (snapshots: SegmentSnapshot[]) => Promise<void>,
): Promise<ProtocolUploadResult> {
  if (!appIdentityKey || !externalHostIdentity) return { kind: 'unavailable' }
  if (snapshots.some((snapshot) => !isUuidV7(snapshot.id))) return { kind: 'unavailable' }
  const renewed = previousSession?.port === port && previousSession.attribution !== undefined
    ? await renewBrowserProtocolSession(previousSession)
    : null
  const activationAttempt = previousActivationAttempt ?? {
    helloMessageId: uuidv7(),
    initializedMessageId: uuidv7(),
    streamsMessageId: uuidv7(),
    readyMessageId: uuidv7(),
  }
  if (renewed === null) await persistActivationAttempt?.(activationAttempt)
  const session = renewed ?? await openBrowserProtocolSession(
    port,
    appIdentityKey,
    externalHostIdentity,
    activationAttempt,
    applySpec,
  )
  if (session === 'disabled') return { kind: 'disabled' }
  if (session === 'rejected') return { kind: 'unavailable' }
  if (session === null) return { kind: 'unavailable', activationAttempt }
  if (session.attribution === undefined) return { kind: 'unavailable' }
  await applyAttribution?.(session.attribution)
  if (recoveryFactIds?.length && applyRecoveredFacts) {
    // Recovery is independent of unrelated new observations; missing/offline evidence retains
    // the old fold while the normal outbox may continue delivering other windows.
    const recovered = await recoverBrowserFacts(session, recoveryFactIds)
    if (recovered !== null) await applyRecoveredFacts(recovered)
  }
  let gapAcknowledged = false
  if (pendingGap !== undefined) {
    const gapResult = await reportBrowserGap(session, pendingGap, persistGapAttempt)
    if (gapResult !== 'acked') {
      return {
        kind: 'unavailable',
        session,
      }
    }
    gapAcknowledged = true
  }
  const result = await publishBrowserFacts(
    session,
    snapshots,
    renewed === null && previousSession !== undefined ? undefined : previousPublishAttempt,
    persistPublishAttempt,
  )
  return result.kind === 'acked' || result.kind === 'unavailable'
    ? { ...result, ...(gapAcknowledged ? { gapAcknowledged: true } : {}) }
    : result
}

export async function reportBrowserGap(
  session: BrowserProtocolSession,
  gap: BrowserPendingGap,
  persistAttempt?: (gap: BrowserPendingGap) => Promise<void>,
): Promise<'acked' | 'unavailable' | 'rejected'> {
  const attempt = gap.activationId === session.activationId && gap.messageId !== undefined
    ? gap
    : { ...gap, activationId: session.activationId, messageId: uuidv7() }
  await persistAttempt?.(attempt)
  try {
    const response = await protocolFetch(
      session.port, `/${session.activationId}/gap`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(message(
          'heartbeat.collector/1',
          'stream.gap',
          attempt.messageId!,
          session.activationId,
          {
            leaseToken: session.leaseToken,
            streamId: session.streamId,
            gap: {
              gapId: attempt.gapId,
              start: attempt.start,
              end: attempt.end,
              reason: attempt.reason,
              estimatedFactsLost: attempt.estimatedFactsLost,
            },
          },
        )),
      },
    )
    if (!response.ok) return 'rejected'
    const acknowledgement = (await response.json()) as ProtocolMessage<{ streamId?: string }>
    return acknowledgement.protocol === 'heartbeat.collector/1' &&
      acknowledgement.type === 'stream.gapAck' &&
      acknowledgement.activationId === session.activationId &&
      acknowledgement.replyTo === attempt.messageId &&
      acknowledgement.body.streamId === session.streamId
      ? 'acked'
      : 'unavailable'
  } catch {
    return 'unavailable'
  }
}

function takeBatchWithinByteLimit(
  snapshots: SegmentSnapshot[],
  session: BrowserProtocolSession,
  maxFacts: number,
): SegmentSnapshot[] {
  const limit = normalizeLimits(session.limits).maxBatchBytes
  const batch: SegmentSnapshot[] = []
  for (const snapshot of snapshots.slice(0, maxFacts)) {
    const candidate = [...batch, snapshot]
    const facts = candidate.map((item) => toProtocolFact(item, session.streamId))
    const logicalMessage = {
      protocol: 'heartbeat.collector/1',
      type: 'facts.publish',
      messageId: '00000000-0000-7000-8000-000000000000',
      activationId: session.activationId,
      body: { facts },
    }
    if (dotNetJsonUpperBoundBytes(logicalMessage) > limit) {
      if (batch.length === 0) continue
      break
    }
    batch.push(snapshot)
  }
  return batch
}

function dotNetJsonUpperBoundBytes(value: unknown): number {
  const json = JSON.stringify(value)
  let bytes = 0
  for (let index = 0; index < json.length; index += 1) {
    const code = json.charCodeAt(index)
    // System.Text.Json's default encoder escapes non-Basic-Latin and HTML-sensitive characters.
    bytes += code > 0x7f || code === 0x2b || code === 0x3c || code === 0x3e || code === 0x26 || code === 0x27
      ? 6
      : 1
  }
  return bytes
}

function normalizeLimits(limits: Partial<ProtocolLimits> | undefined): ProtocolLimits {
  return {
    maxFactsPerBatch: positiveInteger(limits?.maxFactsPerBatch) ?? DEFAULT_LIMITS.maxFactsPerBatch,
    maxBatchBytes: positiveInteger(limits?.maxBatchBytes) ?? DEFAULT_LIMITS.maxBatchBytes,
  }
}

function positiveInteger(value: unknown): number | undefined {
  return Number.isSafeInteger(value) && Number(value) > 0 ? Number(value) : undefined
}

function isCorrelatedResponse<T>(
  response: ProtocolMessage<T>,
  protocol: string,
  type: string,
  activationId: string | undefined,
  replyTo: string | undefined,
): boolean {
  return response?.protocol === protocol && response.type === type &&
    isUuidV7(response.messageId) && response.activationId === activationId &&
    response.replyTo === replyTo && response.body !== undefined
}

function hasCompleteFactResults(acknowledgement: AckResponse, factCount: number): boolean {
  if (!Array.isArray(acknowledgement?.results) || acknowledgement.results.length !== factCount)
    return false
  const indices = acknowledgement.results.map((result) => result.index).sort((left, right) => left - right)
  if (!indices.every((index, position) => index === position)) return false
  return acknowledgement.results.every((result) => {
    if (!['committed', 'duplicate', 'superseded', 'rejected', 'retry'].includes(result.status)) return false
    if (result.status === 'retry') return positiveInteger(result.retryAfterMs) !== undefined
    return result.retryAfterMs === undefined
  })
}

function message<T>(
  protocol: string,
  type: string,
  messageId: string,
  activationId: string | undefined,
  body: T,
  replyTo?: string,
): ProtocolMessage<T> {
  return {
    protocol,
    type,
    messageId,
    ...(activationId === undefined ? {} : { activationId }),
    ...(replyTo === undefined ? {} : { replyTo }),
    body,
  }
}

function isUuid(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000'
}

export function browserAttribution(collectorId: string, machineKey: string, appIdentityKey: string): BrowserAttribution {
  const foi = { kind: 'app' as const, scope: 'heartbeat.app-identity', key: appIdentityKey }
  return { collectorId, foi, relations: [{ kind: 'observed-on', members: [
    { role: 'device', object: { kind: 'machine', scope: 'heartbeat.device', key: machineKey } },
    { role: 'app', object: foi },
  ] }] }
}

/** Only the persisted pre-object format carries Observer/Target. */
export function readAttribution(value: unknown): BrowserAttribution | undefined {
  if (!value || typeof value !== 'object') return undefined
  const saved = value as Record<string, unknown>
  if (saved.collectorId !== undefined || saved.foi !== undefined || saved.relations !== undefined)
    return { collectorId: saved.collectorId, foi: saved.foi, relations: saved.relations } as BrowserAttribution
  const target = saved.target as { kind?: string; reference?: string } | undefined
  if (saved.observerId === undefined && target === undefined) return undefined
  if (typeof saved.observerId !== 'string' || target?.kind !== 'application-context' || typeof target.reference !== 'string')
    throw new Error('Incomplete historical Browser attribution; preserve the cached snapshot')
  const pair: unknown = JSON.parse(target.reference)
  if (!Array.isArray(pair) || pair.length !== 2 || pair.some(item => typeof item !== 'string' || !item))
    throw new Error('Invalid historical Browser attribution; preserve the cached snapshot')
  return browserAttribution(saved.observerId, pair[0], pair[1])
}

export function bindAttribution(snapshot: SegmentSnapshot, attribution?: BrowserAttribution): SegmentSnapshot {
  if (snapshot.collectorId !== undefined || snapshot.foi !== undefined || snapshot.relations !== undefined || attribution === undefined) return snapshot
  return { ...snapshot, ...attribution }
}

export function sameSnapshot(left: SegmentSnapshot, right: SegmentSnapshot): boolean {
  return canonicalJson(left) === canonicalJson(right)
}

/** Object member order is immaterial; every saved result and envelope member participates in ACK. */
function canonicalJson(value: unknown): string {
  return JSON.stringify(value, (_key, item) => item && typeof item === 'object' && !Array.isArray(item)
    ? Object.fromEntries(Object.keys(item).sort().map(key => [key, item[key]])) : item)
}

/** Read exact legacy delivery identities under this Activation's writer lease. Never ACKs. */
export async function recoverBrowserFacts(session: BrowserProtocolSession, factIds: string[]): Promise<SegmentSnapshot[] | null> {
  const requested = factIds.slice(0, session.limits.maxFactsPerBatch)
  const messageId = uuidv7()
  try {
    const response = await protocolFetch(session.port, `/${session.activationId}/recover`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(message('heartbeat.collector/1', 'facts.recover', messageId, session.activationId,
        { leaseToken: session.leaseToken, streamId: session.streamId, factIds: requested })),
    })
    if (!response.ok) return null
    const reply = await response.json() as ProtocolMessage<{ facts: (NonNullable<ReturnType<typeof toProtocolFact>> & { kind?: string | null; streamId: string })[]; missing: string[] }>
    if (!isCorrelatedResponse(reply, 'heartbeat.collector/1', 'facts.recovered', session.activationId, messageId) ||
        !Array.isArray(reply.body.facts) || !Array.isArray(reply.body.missing)) return null
    const received = [...reply.body.facts.map(fact => fact?.factId), ...reply.body.missing]
    if (received.length !== requested.length || new Set(received).size !== requested.length ||
        received.some(id => !requested.includes(id!))) return null
    const snapshots: SegmentSnapshot[] = []
    for (const fact of reply.body.facts) {
      if (!fact || fact.kind != null || fact.streamId !== session.streamId || !Number.isSafeInteger(fact.revision) || fact.revision <= 0 ||
          !fact.time || !Number.isFinite(Date.parse(fact.time.start)) || !Number.isFinite(Date.parse(fact.time.end)) ||
          typeof fact.time.isFinal !== 'boolean' || !fact.payload || typeof fact.payload.activityKey !== 'string' ||
          typeof fact.payload.title !== 'string' || !fact.payload.attributes || !Number.isInteger(fact.payload.attributes.windowId)) return null
      snapshots.push({ ...fact.payload, result: fact.payload, id: fact.factId, source: 'browser', streamId: fact.streamId,
        revision: fact.revision, startTime: fact.time.start, endTime: fact.time.end, isFinal: fact.time.isFinal,
        observedAt: fact.observedAt, aspect: fact.aspect, ...readAttribution(fact) })
    }
    return snapshots
  } catch { return null }
}
