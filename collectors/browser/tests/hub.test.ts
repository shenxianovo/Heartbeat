import { afterEach, describe, expect, it, vi } from 'vitest'
import { discoverHub, fetchCollectorConfig, PORT_RANGE, postToHub, probeHub } from '../src/hub'
import type { SegmentSnapshot } from '../src/fold'

const BASE = 24820

const SEGMENTS = [{ id: 's1' } as unknown as SegmentSnapshot]

/** 各端口的模拟行为：hub = 真 hub；stranger(status) = 陌生服务；其余端口连接失败。 */
type PortBehavior = { kind: 'hub'; postStatus?: number } | { kind: 'stranger'; status: number }

function installFetchMock(ports: Record<number, PortBehavior>): { calls: string[] } {
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push(`${init?.method ?? 'GET'} ${url}`)
      const port = Number(new URL(url).port)
      const behavior = ports[port]
      if (!behavior) throw new TypeError('fetch failed')

      const isProbe = url.endsWith('/v1/hub') && (init?.method ?? 'GET') === 'GET'
      if (behavior.kind === 'stranger') {
        return new Response('not found', { status: behavior.status })
      }
      if (isProbe) {
        return Response.json({ app: 'heartbeat', proto: 1 })
      }
      const status = behavior.postStatus ?? 200
      return new Response(status === 200 ? '{"accepted":1}' : 'rejected', { status })
    }),
  )
  return { calls }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('probeHub', () => {
  it('身份应答正确 → true', async () => {
    installFetchMock({ [BASE]: { kind: 'hub' } })
    await expect(probeHub(BASE)).resolves.toBe(true)
  })

  it('陌生服务（非 heartbeat 应答）→ false', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ app: 'something-else' })),
    )
    await expect(probeHub(BASE)).resolves.toBe(false)
  })

  it('连接失败 → false', async () => {
    installFetchMock({})
    await expect(probeHub(BASE)).resolves.toBe(false)
  })

  it('非 JSON 应答 → false', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('<html>router admin</html>', { status: 200 })),
    )
    await expect(probeHub(BASE)).resolves.toBe(false)
  })
})

describe('discoverHub', () => {
  it('hub 顺延到范围内端口时找到它', async () => {
    installFetchMock({ [BASE + 3]: { kind: 'hub' } })
    await expect(discoverHub(BASE)).resolves.toBe(BASE + 3)
  })

  it('低编号优先：多端口应答时取最小', async () => {
    installFetchMock({ [BASE + 1]: { kind: 'hub' }, [BASE + 5]: { kind: 'hub' } })
    await expect(discoverHub(BASE)).resolves.toBe(BASE + 1)
  })

  it('范围内无 hub → null（陌生服务不算）', async () => {
    installFetchMock({ [BASE]: { kind: 'stranger', status: 404 } })
    await expect(discoverHub(BASE)).resolves.toBe(null)
  })

  it('探测范围与 Agent 约定一致', () => {
    expect(PORT_RANGE).toBe(10)
  })
})

describe('postToHub', () => {
  it('目标端口即 hub：直连成功，不发现', async () => {
    const { calls } = installFetchMock({ [BASE]: { kind: 'hub' } })
    await expect(postToHub(BASE, BASE, SEGMENTS)).resolves.toEqual({ result: 'ok', port: BASE })
    expect(calls).toEqual([`POST http://127.0.0.1:${BASE}/v1/segments`])
  })

  it('目标不可达、hub 顺延在别的端口：发现并改投', async () => {
    installFetchMock({ [BASE + 2]: { kind: 'hub' } })
    await expect(postToHub(BASE, BASE, SEGMENTS)).resolves.toEqual({
      result: 'ok',
      port: BASE + 2,
    })
  })

  it('真 hub 的 4xx：验身份后确认 rejected', async () => {
    installFetchMock({ [BASE]: { kind: 'hub', postStatus: 400 } })
    await expect(postToHub(BASE, BASE, SEGMENTS)).resolves.toEqual({
      result: 'rejected',
      port: BASE,
    })
  })

  it('陌生服务占端口返回 4xx：不判 rejected（保队列），改投真 hub', async () => {
    installFetchMock({
      [BASE]: { kind: 'stranger', status: 404 },
      [BASE + 1]: { kind: 'hub' },
    })
    await expect(postToHub(BASE, BASE, SEGMENTS)).resolves.toEqual({ result: 'ok', port: BASE + 1 })
  })

  it('陌生服务占端口、范围内无 hub → unreachable，队列语义保留', async () => {
    installFetchMock({ [BASE]: { kind: 'stranger', status: 404 } })
    await expect(postToHub(BASE, BASE, SEGMENTS)).resolves.toEqual({
      result: 'unreachable',
      port: BASE,
    })
  })

  it('整个范围无人监听 → unreachable', async () => {
    installFetchMock({})
    await expect(postToHub(BASE, BASE, SEGMENTS)).resolves.toEqual({
      result: 'unreachable',
      port: BASE,
    })
  })

  it('缓存端口失效、hub 回到基准端口：发现并回投', async () => {
    installFetchMock({ [BASE]: { kind: 'hub' } })
    await expect(postToHub(BASE, BASE + 4, SEGMENTS)).resolves.toEqual({
      result: 'ok',
      port: BASE,
    })
  })
})

describe('fetchCollectorConfig', () => {
  it('拉取成功：带 source 与 flushPeriodMs，返回 enabled（ADR-026 §2）', async () => {
    const calls: string[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        calls.push(String(input))
        return Response.json({ enabled: true })
      }),
    )

    await expect(fetchCollectorConfig(BASE, 'browser', 30_000)).resolves.toEqual({ enabled: true })
    expect(calls).toEqual([
      `http://127.0.0.1:${BASE}/v1/collectors/browser/config?flushPeriodMs=30000`,
    ])
  })

  it('hub 侧已停用 → enabled:false（礼貌层据此自停）', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({ enabled: false })),
    )
    await expect(fetchCollectorConfig(BASE, 'browser', 30_000)).resolves.toEqual({
      enabled: false,
    })
  })

  it('连接失败 → null（调用方保守视为未停用）', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('fetch failed')
      }),
    )
    await expect(fetchCollectorConfig(BASE, 'browser', 30_000)).resolves.toBe(null)
  })

  it('旧版 hub 无此路由（404）→ null，不误停', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('not found', { status: 404 })),
    )
    await expect(fetchCollectorConfig(BASE, 'browser', 30_000)).resolves.toBe(null)
  })

  it('应答缺 enabled 字段 → 视为启用（未来字段扩展不破坏契约）', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Response.json({})),
    )
    await expect(fetchCollectorConfig(BASE, 'browser', 30_000)).resolves.toEqual({ enabled: true })
  })
})
