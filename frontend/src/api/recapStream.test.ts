// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { authStore } from '../stores/auth'
import { ApiException } from './client'
import { fetchDailyRecap, recapGenerationErrorMessage, streamDailyRecapGeneration } from './index'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: 'tok-1' },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

const encoder = new TextEncoder()

/** 一条一次性吐完的假 SSE 流。 */
function sseBody(text: string): ReadableStream<Uint8Array> {
  return new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(encoder.encode(text))
      controller.close()
    },
  })
}

/** 吐完给定分块后就挂住的流；abort 时按浏览器的行为让在途的 read 以 AbortError 失败。 */
function hangingBody(chunks: string[], signal: AbortSignal): ReadableStream<Uint8Array> {
  let i = 0
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (i < chunks.length) {
        controller.enqueue(encoder.encode(chunks[i++]))
        return
      }
      return new Promise<void>((_resolve, reject) => {
        signal.addEventListener(
          'abort',
          () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' })),
          { once: true },
        )
      })
    },
  })
}

/** 只实现 wrapper 用到的那几个成员：ok / status / body / text。 */
function response(init: { ok?: boolean; status?: number; body?: ReadableStream<Uint8Array> | null; text?: string }): Response {
  return {
    ok: init.ok ?? true,
    status: init.status ?? 200,
    body: init.body ?? null,
    text: async () => init.text ?? '',
  } as unknown as Response
}

const fetchMock = vi.fn()

const dayWindow = {
  version: 1,
  kind: 'day',
  localDate: '2026-08-19',
  timeZone: 'Asia/Shanghai',
  start: '2026-08-18T16:00:00Z',
  endExclusive: '2026-08-19T16:00:00Z',
} as const

function collect() {
  const deltas: string[] = []
  const thinkings: string[] = []
  const errors: string[] = []
  const done: unknown[] = []
  return {
    deltas, thinkings, errors, done,
    handlers: {
      onThinking: (t: string) => thinkings.push(t),
      onDelta: (t: string) => deltas.push(t),
      onError: (m: string) => errors.push(m),
      onDone: (r: unknown) => done.push(r),
    },
  }
}

describe('Recap 流式生成 wrapper', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
  })

  it('分派 delta 与 done，吞掉心跳与未知事件类型', async () => {
    fetchMock.mockResolvedValue(response({
      body: sseBody(
        'event: ping\ndata: {}\n\n'
        + 'event: delta\ndata: {"delta":"上午你在写代码"}\n\n'
        + 'event: unknown-future-thing\ndata: {"whatever":1}\n\n'
        + 'event: delta\ndata: {"delta":"，下午读文档。"}\n\n'
        + 'event: done\ndata: {"recap":{"date":"2026-08-19","isEmpty":false,"narrative":"上午你在写代码，下午读文档。","segmentStale":false,"knowledgeStale":false}}\n\n',
      ),
    }))
    const sink = collect()

    await streamDailyRecapGeneration({ window: dayWindow }, sink.handlers)

    expect(sink.deltas).toEqual(['上午你在写代码', '，下午读文档。'])
    expect(sink.errors).toEqual([])
    expect(sink.done).toHaveLength(1)
    expect((sink.done[0] as { narrative?: string }).narrative).toBe('上午你在写代码，下午读文档。')
  })

  it('thinking 走 onThinking：推理与正文是两条通道，正文里不许混进推理', async () => {
    fetchMock.mockResolvedValue(response({
      body: sseBody(
        'event: thinking\ndata: {"thinking":"先看 digest："}\n\n'
        + 'event: thinking\ndata: {"thinking":"上午都在 vscode。"}\n\n'
        + 'event: delta\ndata: {"delta":"上午你在写代码。"}\n\n',
      ),
    }))
    const sink = collect()

    await streamDailyRecapGeneration({ window: dayWindow }, sink.handlers)

    expect(sink.thinkings).toEqual(['先看 digest：', '上午都在 vscode。'])
    expect(sink.deltas).toEqual(['上午你在写代码。'])
  })

  it('thinking 为空串或非字符串时不触发（别拿空白顶出一个空的思考面板）', async () => {
    fetchMock.mockResolvedValue(response({
      body: sseBody(
        'event: thinking\ndata: {"thinking":""}\n\n'
        + 'event: thinking\ndata: {"thinking":42}\n\n'
        + 'event: thinking\ndata: {}\n\n'
        + 'event: thinking\ndata: {"thinking":"真的推理"}\n\n',
      ),
    }))
    const sink = collect()

    await streamDailyRecapGeneration({ window: dayWindow }, sink.handlers)

    expect(sink.thinkings).toEqual(['真的推理'])
  })

  it('POST 到 generate 端点，编码完整 Calendar Window 且不携带 correlation identity', async () => {
    fetchMock.mockResolvedValue(response({ body: sseBody('') }))

    await streamDailyRecapGeneration({ window: dayWindow }, {})

    const [url, init] = fetchMock.mock.calls[0]
    const parsed = new URL(url, 'https://heartbeat.test')
    expect(parsed.pathname).toBe('/api/v1/recaps/daily/generate')
    expect(Object.fromEntries(parsed.searchParams)).toEqual({
      version: '1',
      kind: 'day',
      localDate: '2026-08-19',
      timeZone: 'Asia/Shanghai',
      start: '2026-08-18T16:00:00Z',
      endExclusive: '2026-08-19T16:00:00Z',
    })
    expect(parsed.searchParams.has('correlationIdentity')).toBe(false)
    expect(init.method).toBe('POST')
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok-1')
  })

  it('generated GET 与手写 SSE 编码同一个 Calendar Window', async () => {
    fetchMock
      .mockResolvedValueOnce(response({
        text: JSON.stringify({ date: '2026-08-19', isEmpty: true }),
      }))
      .mockResolvedValueOnce(response({ body: sseBody('') }))

    await fetchDailyRecap({ window: dayWindow })
    await streamDailyRecapGeneration({ window: dayWindow }, {})

    const readUrl = new URL(fetchMock.mock.calls[0][0], 'https://heartbeat.test')
    const streamUrl = new URL(fetchMock.mock.calls[1][0], 'https://heartbeat.test')
    const read = Object.fromEntries(
      [...readUrl.searchParams].map(([key, value]) => [key.toLowerCase(), value]),
    )
    const stream = Object.fromEntries(
      [...streamUrl.searchParams].map(([key, value]) => [key.toLowerCase(), value]),
    )

    expect(read.version).toBe(stream.version)
    expect(read.kind).toBe(stream.kind)
    expect(read.localdate).toBe(stream.localdate)
    expect(read.timezone).toBe(stream.timezone)
    expect(new Date(read.start).toISOString()).toBe(new Date(stream.start).toISOString())
    expect(new Date(read.endexclusive).toISOString()).toBe(new Date(stream.endexclusive).toISOString())
    expect(readUrl.searchParams.has('correlationIdentity')).toBe(false)
    expect(streamUrl.searchParams.has('correlationIdentity')).toBe(false)
  })

  it('流内 error 走 onError，不抛（生成域的失败不再是状态码）', async () => {
    fetchMock.mockResolvedValue(response({
      body: sseBody('event: delta\ndata: {"delta":"半截"}\n\nevent: error\ndata: {"message":"上游模型 90 秒未吐出首个 token"}\n\n'),
    }))
    const sink = collect()

    await expect(streamDailyRecapGeneration({ window: dayWindow }, sink.handlers)).resolves.toBeUndefined()

    expect(sink.deltas).toEqual(['半截'])
    expect(sink.errors).toEqual(['上游模型 90 秒未吐出首个 token'])
    expect(sink.done).toEqual([])
  })

  it('并发撞锁的 409 抛 ApiException，上层能翻成"这一天正在生成中"', async () => {
    fetchMock.mockResolvedValue(response({ ok: false, status: 409, text: '"这一天正在生成中。"' }))

    const error = await streamDailyRecapGeneration({ window: dayWindow }, {}).catch((e: unknown) => e)

    expect(ApiException.isApiException(error)).toBe(true)
    expect((error as ApiException).status).toBe(409)
    expect(recapGenerationErrorMessage(error)).toBe('这一天正在生成中。')
  })

  it('SSE 头前的 calendar mismatch 保留稳定诊断', async () => {
    fetchMock.mockResolvedValue(response({
      ok: false,
      status: 400,
      text: JSON.stringify({
        code: 'calendar_rules_mismatch',
        message: 'Browser 与 Analytics TZDB 不一致，请更新滞后的运行时。',
      }),
    }))

    const error = await streamDailyRecapGeneration({ window: dayWindow }, {}).catch((e: unknown) => e)

    expect(recapGenerationErrorMessage(error)).toBe(
      'Browser 与 Analytics TZDB 不一致，请更新滞后的运行时。',
    )
  })

  it('abort 后静默收场：既不抛，也不再吐 delta', async () => {
    const controller = new AbortController()
    fetchMock.mockImplementation(() => Promise.resolve(response({
      body: hangingBody(['event: delta\ndata: {"delta":"第一块"}\n\n'], controller.signal),
    })))
    const sink = collect()

    const streamed = streamDailyRecapGeneration({ window: dayWindow, signal: controller.signal }, sink.handlers)
    await vi.waitUntil(() => sink.deltas.length === 1)
    controller.abort()

    await expect(streamed).resolves.toBeUndefined()
    expect(sink.deltas).toEqual(['第一块'])
    expect(sink.errors).toEqual([])
  })

  it('401 在读流之前刷新并重试一次；流已经开始就不再重试', async () => {
    vi.mocked(authStore.tryRefresh).mockResolvedValue(true)
    fetchMock
      .mockResolvedValueOnce(response({ ok: false, status: 401 }))
      .mockResolvedValueOnce(response({ body: sseBody('event: delta\ndata: {"delta":"刷新后拿到的正文"}\n\n') }))
    const sink = collect()

    await streamDailyRecapGeneration({ window: dayWindow }, sink.handlers)

    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(sink.deltas).toEqual(['刷新后拿到的正文'])
  })
})
