import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import {
  acknowledgedSnapshotIds,
  snapshotRevision,
  toProtocolFact,
  uploadWithBrowserProtocol,
} from '../src/protocol'

const snapshot = (id = '0198d5eb-fc31-7d7b-8bf0-c2d009ec8999'): SegmentSnapshot => ({
  id,
  source: 'browser',
  identityKey: 'https://example.com/docs',
  appHint: 'edge',
  title: 'Docs',
  startTime: '2026-08-25T08:00:00.000Z',
  endTime: '2026-08-25T08:01:00.000Z',
  attributes: { url: 'https://example.com/docs?q=1', domain: 'example.com', site: 'example.com', windowId: 7 },
})

afterEach(() => vi.unstubAllGlobals())

describe('browser Collector Protocol outbox', () => {
  it('canonical Fact excludes AppHint while preserving typed browser payload', () => {
    const fact = toProtocolFact(snapshot(), '0198d5e2-e0d4-7b30-9da7-342ee261bf62')!
    expect(fact.payload).toEqual({
      identityKey: 'https://example.com/docs',
      title: 'Docs',
      attributes: { url: 'https://example.com/docs?q=1', domain: 'example.com', site: 'example.com', windowId: 7 },
    })
    expect(fact.payload).not.toHaveProperty('appHint')
    expect(fact.revision).toBe(snapshotRevision(snapshot()))
  })

  it('only explicitly acknowledged results select outbox entries for deletion', () => {
    const items = [snapshot(), snapshot('0198d5eb-fc31-7d7b-8bf0-c2d009ec8998')]
    expect(acknowledgedSnapshotIds(items, {
      results: [
        { index: 0, status: 'committed' },
        { index: 1, status: 'rejected' },
      ],
    })).toEqual([items[0].id])
  })

  it('old non-UUIDv7 cache requests legacy adapter without deleting the queue', async () => {
    await expect(uploadWithBrowserProtocol(24820, 'edge', [snapshot('legacy-id')])).resolves.toEqual({
      kind: 'legacy-required',
    })
  })

  it('happy path negotiates Spec, opens Stream, and returns per-Fact ACK identities', async () => {
    const calls: { url: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined })
      if (url.endsWith('/hello')) return Response.json({
        activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
        spec: { specRevision: 3, config: { enabled: true } },
      })
      if (url.endsWith('/ready')) return Response.json({
        streams: { tabs: { streamId: '0198d5e2-e0d4-7b30-9da7-342ee261bf62' } },
        lease: { token: 'lease', expiresAt: '2026-08-25T08:01:00Z' },
      })
      return Response.json({ results: [{ index: 0, status: 'committed' }] })
    }))

    const result = await uploadWithBrowserProtocol(24820, 'edge', [snapshot()])

    expect(result.kind).toBe('acked')
    if (result.kind === 'acked') expect(result.acknowledgedIds).toEqual([snapshot().id])
    expect(calls.map((call) => call.url.split('/').at(-1))).toEqual(['hello', 'ready', 'facts'])
    expect((calls[2].body as { facts: { payload: object }[] }).facts[0].payload).not.toHaveProperty('appHint')
  })
})
