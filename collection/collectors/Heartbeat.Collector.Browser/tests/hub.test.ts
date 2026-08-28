import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import {
  discoverHub,
  fetchCollectorConfig,
  PORT_RANGE,
  postToHub,
  probeHub,
  REQUIRED_HUB_PROTOCOL,
} from '../src/hub'

const BASE = 24_820
const SEGMENT: SegmentSnapshot = {
  id: '0198d5eb-fc31-7d7b-8bf0-000000000001',
  source: 'browser',
  identityKey: 'https://example.com/page',
  appHint: 'edge',
  title: 'Example',
  startTime: '2026-08-11T00:00:00.000Z',
  endTime: '2026-08-11T00:01:00.000Z',
  isFinal: false,
  attributes: {
    url: 'https://example.com/page?q=1',
    domain: 'example.com',
    site: 'example.com',
    windowId: 7,
  },
}

type PortBehavior =
  | { kind: 'hub'; proto?: number; postStatus?: number }
  | { kind: 'stranger'; status: number }

function installFetchMock(ports: Record<number, PortBehavior>) {
  const calls: string[] = []
  const bodies: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push(`${init?.method ?? 'GET'} ${url}`)
    if (init?.body) bodies.push(String(init.body))
    const behavior = ports[Number(new URL(url).port)]
    if (!behavior) throw new TypeError('fetch failed')
    if (behavior.kind === 'stranger') return new Response('not found', { status: behavior.status })
    if (url.endsWith('/v1/hub')) {
      return Response.json({ app: 'heartbeat', proto: behavior.proto ?? REQUIRED_HUB_PROTOCOL })
    }
    return new Response('accepted', { status: behavior.postStatus ?? 200 })
  }))
  return { calls, bodies }
}

afterEach(() => vi.unstubAllGlobals())

describe('Loopback Hub wire adapter', () => {
  it('accepts only the exact Heartbeat identity and protocol', async () => {
    installFetchMock({ [BASE]: { kind: 'hub' } })
    await expect(probeHub(BASE)).resolves.toBe(true)

    installFetchMock({ [BASE]: { kind: 'hub', proto: REQUIRED_HUB_PROTOCOL - 1 } })
    await expect(probeHub(BASE)).resolves.toBe(false)

    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ app: 'other', proto: REQUIRED_HUB_PROTOCOL })))
    await expect(probeHub(BASE)).resolves.toBe(false)
  })

  it('discovers the lowest compatible port in the shared range', async () => {
    installFetchMock({
      [BASE + 1]: { kind: 'hub', proto: REQUIRED_HUB_PROTOCOL - 1 },
      [BASE + 2]: { kind: 'hub' },
      [BASE + 4]: { kind: 'hub' },
    })
    await expect(discoverHub(BASE)).resolves.toBe(BASE + 2)
    expect(PORT_RANGE).toBe(10)
  })

  it('returns null when only strangers or unreachable ports exist', async () => {
    installFetchMock({ [BASE]: { kind: 'stranger', status: 404 } })
    await expect(discoverHub(BASE)).resolves.toBe(null)
  })

  it('verifies identity before legacy POST and preserves the payload contract', async () => {
    const { calls, bodies } = installFetchMock({ [BASE]: { kind: 'hub' } })

    await expect(postToHub(BASE, BASE, [SEGMENT])).resolves.toEqual({ result: 'ok', port: BASE })

    expect(calls).toEqual([
      `GET http://127.0.0.1:${BASE}/v1/hub`,
      `POST http://127.0.0.1:${BASE}/v1/segments`,
    ])
    const posted = JSON.parse(bodies[0]) as { segments: Record<string, unknown>[] }
    expect(posted.segments[0]).toMatchObject({ source: 'browser', appHint: 'edge' })
    expect(posted.segments[0]).not.toHaveProperty('appName')
  })

  it('never POSTs to an incompatible old Hub even if it would return 2xx', async () => {
    const { calls } = installFetchMock({
      [BASE]: { kind: 'hub', proto: REQUIRED_HUB_PROTOCOL - 1, postStatus: 200 },
    })
    await expect(postToHub(BASE, BASE, [SEGMENT])).resolves.toEqual({
      result: 'unreachable',
      port: BASE,
    })
    expect(calls.every((call) => call.startsWith('GET '))).toBe(true)
  })

  it('skips a stranger and redirects legacy delivery to the compatible Hub', async () => {
    installFetchMock({
      [BASE]: { kind: 'stranger', status: 404 },
      [BASE + 1]: { kind: 'hub' },
    })
    await expect(postToHub(BASE, BASE, [SEGMENT])).resolves.toEqual({
      result: 'ok',
      port: BASE + 1,
    })
  })

  it('maps an authenticated Hub 4xx to legacy rejection', async () => {
    installFetchMock({ [BASE]: { kind: 'hub', postStatus: 400 } })
    await expect(postToHub(BASE, BASE, [SEGMENT])).resolves.toEqual({
      result: 'rejected',
      port: BASE,
    })
  })

  it('sends legacy config registration fields and maps enabled', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      calls.push(String(input))
      return Response.json({ enabled: false })
    }))
    await expect(fetchCollectorConfig(BASE, 'browser', 60_000)).resolves.toEqual({ enabled: false })
    expect(calls).toEqual([
      `http://127.0.0.1:${BASE}/v1/collectors/browser/config?flushPeriodMs=60000`,
    ])
  })

  it('maps failed legacy config reads to unknown, never to disabled', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('fetch failed') }))
    await expect(fetchCollectorConfig(BASE, 'browser', 30_000)).resolves.toBe(null)
  })
})
