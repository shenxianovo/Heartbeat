import { afterEach, describe, expect, it, vi } from 'vitest'
import { discoverHub, PORT_RANGE, probeHub } from '../src/hub'

const BASE = 24_820

type PortBehavior =
  | { kind: 'binding'; protocolMajors?: number[] }
  | { kind: 'stranger'; status: number }

function installFetchMock(ports: Record<number, PortBehavior>) {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    calls.push(url)
    const behavior = ports[Number(new URL(url).port)]
    if (!behavior) throw new TypeError('fetch failed')
    if (behavior.kind === 'stranger') return new Response('not found', { status: behavior.status })
    return Response.json({ binding: 'external-host', protocolMajors: behavior.protocolMajors ?? [1] })
  }))
  return calls
}

afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs() })

describe('Browser binding discovery', () => {
  it('never discovers a production Desktop when a development binding is missing', async () => {
    vi.stubEnv('MODE', 'development')
    vi.stubGlobal('chrome', { runtime: { getURL: (path: string) => `chrome-extension://dev/${path}` } })
    const calls = installFetchMock({ [BASE]: { kind: 'binding' } })
    await expect(discoverHub(BASE)).resolves.toBe(null)
    expect(calls.filter(url => url.startsWith('http://'))).toEqual([])
  })

  it('accepts only the binding-specific endpoint and a common protocol major', async () => {
    const calls = installFetchMock({ [BASE]: { kind: 'binding' } })
    await expect(probeHub(BASE)).resolves.toBe(true)
    expect(calls).toEqual([`http://127.0.0.1:${BASE}/v1/collector-protocol/external-host`])

    installFetchMock({ [BASE]: { kind: 'binding', protocolMajors: [2] } })
    await expect(probeHub(BASE)).resolves.toBe(false)

    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ binding: 'other', protocolMajors: [1] })))
    await expect(probeHub(BASE)).resolves.toBe(false)
  })

  it('discovers the lowest compatible port in the shared range', async () => {
    installFetchMock({
      [BASE + 1]: { kind: 'binding', protocolMajors: [2] },
      [BASE + 2]: { kind: 'binding' },
      [BASE + 4]: { kind: 'binding' },
    })
    await expect(discoverHub(BASE)).resolves.toBe(BASE + 2)
    expect(PORT_RANGE).toBe(10)
  })

  it('returns null when only strangers or unreachable ports exist', async () => {
    installFetchMock({ [BASE]: { kind: 'stranger', status: 404 } })
    await expect(discoverHub(BASE)).resolves.toBe(null)
  })
})
