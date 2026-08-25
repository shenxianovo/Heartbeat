import type { SegmentSnapshot } from './fold'
import { uuidv7 } from './ids'

const ROUTE = '/v1/collector-protocol/browser'
const ARTIFACT_ID = 'browser.extension'
const ARTIFACT_HASH = 'sha256:0c4d749ffa5d7dc6467c04a66cc054c54433a951b2e00555215d923bf7a14f46'

export interface BrowserProtocolSession {
  port: number
  activationId: string
  leaseToken: string
  streamId: string
  specRevision: number
  expiresAt: string
  limits: ProtocolLimits
}

export interface BrowserActivationAttempt {
  helloMessageId: string
  initializedMessageId: string
  streamsMessageId: string
  readyMessageId: string
}

export interface BrowserPublishAttempt {
  messageId: string
  snapshots: SegmentSnapshot[]
}

interface ProtocolLimits {
  maxFactsPerBatch: number
  maxBatchBytes: number
  maxInFlightBatches: number
}

const DEFAULT_LIMITS: ProtocolLimits = {
  maxFactsPerBatch: 500,
  maxBatchBytes: 1_048_576,
  maxInFlightBatches: 1,
}

export type ProtocolUploadResult =
  | {
      kind: 'acked'
      acknowledgedIds: string[]
      acknowledgedRevisions: Record<string, number>
      session: BrowserProtocolSession
    }
  | { kind: 'disabled' }
  | {
      kind: 'unavailable'
      activationAttempt?: BrowserActivationAttempt
      publishAttempt?: BrowserPublishAttempt
      session?: BrowserProtocolSession
    }
  | { kind: 'legacy-required' }

interface HelloResponse {
  activationId: string
}

interface InitializeResponse {
  spec: {
    revision: number
    config: { value: { enabled?: boolean } }
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
  results: { index: number; status: string }[]
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
  const revision = Date.parse(snapshot.endTime)
  return Number.isSafeInteger(revision) && revision > 0 ? revision : 1
}

export function toProtocolFact(snapshot: SegmentSnapshot, streamId: string) {
  if (!isUuidV7(snapshot.id)) return null
  return {
    streamId,
    schemaRevision: 1,
    factId: snapshot.id,
    revision: snapshotRevision(snapshot),
    observedAt: null,
    recordState: 'present',
    time: {
      start: snapshot.startTime,
      end: snapshot.endTime,
      isFinal: snapshot.isFinal,
    },
    payload: {
      identityKey: snapshot.identityKey,
      title: snapshot.title,
      attributes: snapshot.attributes,
    },
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
  appHint: string,
  attempt: BrowserActivationAttempt,
): Promise<BrowserProtocolSession | 'disabled' | 'legacy-required' | 'rejected' | null> {
  try {
    const hello = await fetch(`http://127.0.0.1:${port}${ROUTE}/hello`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(message(
        'heartbeat.collector.bootstrap/1',
        'activation.hello',
        attempt.helloMessageId,
        undefined,
        {
        artifactId: ARTIFACT_ID,
        artifactHash: ARTIFACT_HASH,
        protocolMajors: [1],
        supportedCapabilities: {
          'facts.segment': [1],
          'diagnostics.stream-gap': [1],
        },
        appHint,
      })),
    })
    if (hello.status === 404) return 'legacy-required'
    if (!hello.ok) return 'rejected'
    const accepted = ((await hello.json()) as ProtocolMessage<HelloResponse>).body
    const initialize = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/initialize`,
      { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' },
    )
    if (!initialize.ok) return 'rejected'
    const initializeMessage = (await initialize.json()) as ProtocolMessage<InitializeResponse>
    const initialized = initializeMessage.body
    if (initialized.spec.config.value.enabled === false) return 'disabled'

    const initializedAck = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/initialized`,
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

    const streams = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/streams`,
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
    const opened = ((await streams.json()) as ProtocolMessage<StreamsOpenedResponse>).body
    const stream = opened.streams.tabs
    if (!stream?.streamId) return 'rejected'

    const ready = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/ready`,
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
    const readyAcknowledgement = ((await ready.json()) as ProtocolMessage<ReadyResponse>).body
    if (!readyAcknowledgement.lease?.token) return null
    return {
      port,
      activationId: accepted.activationId,
      leaseToken: readyAcknowledgement.lease.token,
      streamId: stream.streamId,
      specRevision: initialized.spec.revision,
      expiresAt: readyAcknowledgement.lease.expiresAt,
      limits: normalizeLimits(initialized.limits),
    }
  } catch {
    return null
  }
}

export async function renewBrowserProtocolSession(
  session: BrowserProtocolSession,
): Promise<BrowserProtocolSession | null> {
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/renew`,
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
  const batch = previousAttempt?.snapshots ?? takeBatchWithinByteLimit(snapshots, session, maxFacts)
  if (snapshots.length > 0 && batch.length === 0) return { kind: 'unavailable' }
  const facts = batch.map((snapshot) => toProtocolFact(snapshot, session.streamId))
  if (facts.some((fact) => fact === null)) return { kind: 'legacy-required' }
  if (facts.length === 0) {
    return { kind: 'acked', acknowledgedIds: [], acknowledgedRevisions: {}, session }
  }
  const attempt = previousAttempt ?? { messageId: uuidv7(), snapshots: batch }
  await persistAttempt?.(attempt)
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/facts`,
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
    const acknowledgement = ((await response.json()) as ProtocolMessage<AckResponse>).body
    const acknowledgedIds = acknowledgedSnapshotIds(batch, acknowledgement)
    return {
      kind: 'acked',
      acknowledgedIds,
      acknowledgedRevisions: Object.fromEntries(
        acknowledgedIds.map((id) => [
          id,
          snapshotRevision(batch.find((snapshot) => snapshot.id === id)!),
        ]),
      ),
      session,
    }
  } catch {
    return { kind: 'unavailable', publishAttempt: attempt, session }
  }
}

export async function uploadWithBrowserProtocol(
  port: number,
  appHint: string | undefined,
  snapshots: SegmentSnapshot[],
  previousSession?: BrowserProtocolSession,
  previousActivationAttempt?: BrowserActivationAttempt,
  previousPublishAttempt?: BrowserPublishAttempt,
  persistActivationAttempt?: (attempt: BrowserActivationAttempt) => Promise<void>,
  persistPublishAttempt?: (attempt: BrowserPublishAttempt) => Promise<void>,
): Promise<ProtocolUploadResult> {
  if (!appHint) return { kind: 'legacy-required' }
  const renewed = previousSession?.port === port
    ? await renewBrowserProtocolSession(previousSession)
    : null
  const activationAttempt = previousActivationAttempt ?? {
    helloMessageId: uuidv7(),
    initializedMessageId: uuidv7(),
    streamsMessageId: uuidv7(),
    readyMessageId: uuidv7(),
  }
  if (renewed === null) await persistActivationAttempt?.(activationAttempt)
  const session = renewed ?? await openBrowserProtocolSession(port, appHint, activationAttempt)
  if (session === 'disabled') return { kind: 'disabled' }
  if (session === 'legacy-required') return { kind: 'legacy-required' }
  if (session === 'rejected') return { kind: 'unavailable' }
  if (session === null) return { kind: 'unavailable', activationAttempt }
  return publishBrowserFacts(
    session,
    snapshots,
    renewed === null && previousSession !== undefined ? undefined : previousPublishAttempt,
    persistPublishAttempt,
  )
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
    maxInFlightBatches: positiveInteger(limits?.maxInFlightBatches) ?? DEFAULT_LIMITS.maxInFlightBatches,
  }
}

function positiveInteger(value: unknown): number | undefined {
  return Number.isSafeInteger(value) && Number(value) > 0 ? Number(value) : undefined
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
