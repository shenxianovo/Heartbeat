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
}

export type ProtocolUploadResult =
  | { kind: 'acked'; acknowledgedIds: string[]; session: BrowserProtocolSession }
  | { kind: 'disabled' }
  | { kind: 'unavailable' }
  | { kind: 'legacy-required' }

interface HelloResponse {
  activationId: string
  spec: {
    specRevision: number
    config: { enabled?: boolean }
  }
}

interface ReadyResponse {
  streams: Record<string, { streamId: string }>
  lease: { token: string; expiresAt: string }
}

interface AckResponse {
  results: { index: number; status: string }[]
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
      isFinal: false,
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
): Promise<BrowserProtocolSession | 'disabled' | 'legacy-required' | null> {
  try {
    const hello = await fetch(`http://127.0.0.1:${port}${ROUTE}/hello`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        messageId: uuidv7(),
        artifactId: ARTIFACT_ID,
        artifactHash: ARTIFACT_HASH,
        protocolMajors: [1],
        supportedCapabilities: {
          'facts.segment': [1],
          'diagnostics.stream-gap': [1],
        },
        appHint,
      }),
    })
    if (hello.status === 404) return 'legacy-required'
    if (!hello.ok) return null
    const accepted = (await hello.json()) as HelloResponse
    if (accepted.spec.config.enabled === false) return 'disabled'

    const ready = await fetch(
      `http://127.0.0.1:${port}${ROUTE}/${accepted.activationId}/ready`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          messageId: uuidv7(),
          appliedSpecRevision: accepted.spec.specRevision,
          bindings: [{ bindingId: 'tabs', outputId: 'activeTab', dimensions: {} }],
        }),
      },
    )
    if (!ready.ok) return null
    const opened = (await ready.json()) as ReadyResponse
    const stream = opened.streams.tabs
    if (!stream?.streamId || !opened.lease?.token) return null
    return {
      port,
      activationId: accepted.activationId,
      leaseToken: opened.lease.token,
      streamId: stream.streamId,
      specRevision: accepted.spec.specRevision,
      expiresAt: opened.lease.expiresAt,
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
): Promise<ProtocolUploadResult> {
  const batch = snapshots.slice(0, 500)
  const facts = batch.map((snapshot) => toProtocolFact(snapshot, session.streamId))
  if (facts.some((fact) => fact === null)) return { kind: 'legacy-required' }
  try {
    const response = await fetch(
      `http://127.0.0.1:${session.port}${ROUTE}/${session.activationId}/facts`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          messageId: uuidv7(),
          leaseToken: session.leaseToken,
          streamId: session.streamId,
          facts,
        }),
      },
    )
    if (response.status === 403) return { kind: 'disabled' }
    if (!response.ok) return { kind: 'unavailable' }
    const acknowledgement = (await response.json()) as AckResponse
    return {
      kind: 'acked',
      acknowledgedIds: acknowledgedSnapshotIds(batch, acknowledgement),
      session,
    }
  } catch {
    return { kind: 'unavailable' }
  }
}

export async function uploadWithBrowserProtocol(
  port: number,
  appHint: string | undefined,
  snapshots: SegmentSnapshot[],
  previousSession?: BrowserProtocolSession,
): Promise<ProtocolUploadResult> {
  if (!appHint) return { kind: 'legacy-required' }
  const renewed = previousSession?.port === port
    ? await renewBrowserProtocolSession(previousSession)
    : null
  const session = renewed ?? await openBrowserProtocolSession(port, appHint)
  if (session === 'disabled') return { kind: 'disabled' }
  if (session === 'legacy-required') return { kind: 'legacy-required' }
  if (session === null) return { kind: 'unavailable' }
  return publishBrowserFacts(session, snapshots)
}
