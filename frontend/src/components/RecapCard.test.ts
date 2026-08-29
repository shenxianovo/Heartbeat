// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchDailyRecap, fetchPublicDailyRecap, recapGenerationErrorMessage, streamDailyRecapGeneration,
  type DailyRecapResponse,
} from '../api/index'
import RecapCard from './RecapCard.vue'
import { resolveCalendarContext, type CalendarContext, type CalendarWindowEnvelope } from '../calendar/localCalendarWindow'

vi.mock('../api/index', () => ({
  fetchDailyRecap: vi.fn(),
  fetchPublicDailyRecap: vi.fn(),
  streamDailyRecapGeneration: vi.fn(),
  recapGenerationErrorMessage: vi.fn(() => '生成失败，请稍后重试'),
  toApiError: vi.fn((error: unknown) => error),
}))

interface StreamHandlers {
  onThinking?: (text: string) => void
  onDelta?: (text: string) => void
  onDone?: (recap: DailyRecapResponse) => void
  onError?: (message: string) => void
}

interface StreamCall {
  window: CalendarWindowEnvelope<'day'>
  signal?: AbortSignal
  handlers: StreamHandlers
  finish: () => void
}

/** 每次生成都挂住，由测试手工喂事件——流式的时序才是这里要断言的东西。 */
function captureStreams(): StreamCall[] {
  const calls: StreamCall[] = []
  vi.mocked(streamDailyRecapGeneration).mockImplementation((params, handlers) =>
    new Promise<void>(resolve => {
      calls.push({ window: params.window, signal: params.signal, handlers, finish: resolve })
    }))
  return calls
}

function recap(overrides: Partial<DailyRecapResponse> = {}): DailyRecapResponse {
  return {
    date: '2026-08-19',
    isEmpty: false,
    narrative: null,
    generatedAt: null,
    model: null,
    knowledgeStale: false,
    segmentStale: false,
    ...overrides,
  } as unknown as DailyRecapResponse
}

async function mountCard(canRegenerate = true, date = '2026-08-19') {
  const wrapper = mount(RecapCard, {
    props: {
      calendarContext: context(date),
      username: 'alice',
      canRegenerate,
    },
    global: {
      stubs: {
        Card: { template: '<section><slot /></section>' },
        RecapCorrection: true,
      },
    },
  })
  await flushPromises()
  return wrapper
}

function context(date: string) {
  return resolveCalendarContext(date, {
    timeZone: 'Etc/UTC',
    now: '2026-08-19T12:00:00Z',
    correlationIdentity: () => `context-${date}`,
  })
}

function contextWithIdentity(date: string, identity: string) {
  return resolveCalendarContext(date, {
    timeZone: 'Etc/UTC',
    now: '2026-08-19T12:00:00Z',
    correlationIdentity: () => identity,
  })
}

function regenerateButton(wrapper: ReturnType<typeof mount>) {
  return wrapper.findAll('button').find(b => b.text() === '重新生成')
}

function thinkingPanel(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('.recap-thinking-panel')
}

/**
 * happy-dom 不做布局：scrollHeight / clientHeight 恒为 0，scrollTop 的写入也无从观察。
 * 把这三个量钉成可控的自有属性，滚动逻辑才可断言（断言的是逻辑，不是浏览器的排版）。
 */
function stubScrollMetrics(el: Element, sizes: { scrollHeight: number; clientHeight: number }) {
  const state = { scrollTop: 0 }
  Object.defineProperty(el, 'scrollHeight', { value: sizes.scrollHeight, configurable: true })
  Object.defineProperty(el, 'clientHeight', { value: sizes.clientHeight, configurable: true })
  Object.defineProperty(el, 'scrollTop', {
    get: () => state.scrollTop,
    set: (v: number) => { state.scrollTop = v },
    configurable: true,
  })
  return state
}

describe('RecapCard 三态渲染', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    captureStreams()
  })

  it('空日只说没有记录，不发起生成', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ isEmpty: true }))

    const wrapper = await mountCard()

    expect(wrapper.text()).toContain('这一天没有记录。')
    expect(streamDailyRecapGeneration).not.toHaveBeenCalled()
  })

  it('已有叙事且两个判脏位都是 false：直接渲染，不烧 token', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({
      narrative: '上午写代码。\n\n下午读文档。',
      generatedAt: new Date('2026-08-19T10:00:00+08:00') as unknown as Date,
      model: 'deepseek-v4-pro',
    }))

    const wrapper = await mountCard()

    expect(wrapper.findAll('p').map(p => p.text())).toEqual(['上午写代码。', '下午读文档。'])
    expect(wrapper.text()).toContain('deepseek-v4-pro')
    expect(streamDailyRecapGeneration).not.toHaveBeenCalled()
  })

  it('有数据但从未生成：自动发起生成，增量原样追加并按空行重算段落', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap())
    const calls = captureStreams()

    const wrapper = await mountCard()

    expect(calls).toHaveLength(1)
    expect(calls[0].window.localDate).toBe('2026-08-19')
    expect(wrapper.text()).toContain('正在回忆这一天…')

    calls[0].handlers.onDelta?.('上午写代码。')
    calls[0].handlers.onDelta?.('\n\n下午')
    calls[0].handlers.onDelta?.('读文档。')
    await flushPromises()

    expect(wrapper.findAll('p').map(p => p.text())).toEqual(['上午写代码。', '下午读文档。'])
  })

  it('访客视角下从未生成时不触发生成', async () => {
    vi.mocked(fetchPublicDailyRecap).mockResolvedValue(recap())

    const wrapper = await mountCard(false)

    expect(streamDailyRecapGeneration).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('这一天还没有回顾。')
  })
})

describe('Recap 纠正的 captured-window regeneration', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '原窗口叙事。' }))
  })

  it('stream 启动后切 refresh generation 仍完成原窗口生成且不回写新页面', async () => {
    const calls = captureStreams()
    const wrapper = await mountCard()
    const originalContext = wrapper.props('calendarContext') as CalendarContext
    const correction = wrapper.findComponent({ name: 'RecapCorrection' })
    const regenerate = correction.props('regenerate') as (context: CalendarContext) => Promise<void>

    const regenerated = regenerate(originalContext)
    await vi.waitUntil(() => calls.length === 1)

    await wrapper.setProps({ calendarContext: contextWithIdentity('2026-08-18', 'generation-2') })
    await flushPromises()

    expect(calls[0].window).toEqual(originalContext.day)
    expect(calls[0].signal?.aborted).not.toBe(true)
    calls[0].handlers.onDone?.(recap({ date: '2026-08-19', narrative: '旧窗口新叙事。' }))
    calls[0].finish()
    await expect(regenerated).resolves.toBeUndefined()

    expect(wrapper.text()).not.toContain('旧窗口新叙事。')
  })

  it('旧窗口后台生成不会取消新页面已经开始的生成', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '当前页面旧叙事。' }))
    const calls = captureStreams()
    const wrapper = await mountCard()
    await regenerateButton(wrapper)!.trigger('click')
    await vi.waitUntil(() => calls.length === 1)
    const currentWindow = wrapper.props('calendarContext').day
    const correction = wrapper.findComponent({ name: 'RecapCorrection' })
    const regenerate = correction.props('regenerate') as (context: CalendarContext) => Promise<void>
    const oldContext = contextWithIdentity('2026-08-18', 'old-generation')

    const regenerated = regenerate(oldContext)
    await vi.waitUntil(() => calls.length === 2)

    expect(calls[0].window).toEqual(currentWindow)
    expect(calls[0].signal?.aborted).toBe(false)
    expect(calls[1].window).toEqual(oldContext.day)
    expect(calls[1].signal).toBeUndefined()

    calls[1].handlers.onDone?.(recap({ date: '2026-08-18', narrative: '旧窗口已生成。' }))
    calls[1].finish()
    await expect(regenerated).resolves.toBeUndefined()

    calls[0].handlers.onDone?.(recap({ date: '2026-08-19', narrative: '新页面叙事。' }))
    calls[0].finish()
    await flushPromises()
    expect(wrapper.text()).toContain('新页面叙事。')
    expect(wrapper.text()).not.toContain('旧窗口已生成。')
  })
})

describe('RecapCard 自动生成的触发位', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    captureStreams()
  })

  it('segmentStale 触发一次自动生成（阈值判断在服务端，前端只看布尔）', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '旧的叙事。', segmentStale: true }))

    await mountCard()

    expect(streamDailyRecapGeneration).toHaveBeenCalledTimes(1)
  })

  it('knowledgeStale 维持现状：只是判脏位，不自动生成', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '旧的叙事。', knowledgeStale: true }))

    await mountCard()

    expect(streamDailyRecapGeneration).not.toHaveBeenCalled()
  })

  it('done 之后用服务端的完整 DTO 收敛，并解除"重新生成"的禁用', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '旧的叙事。', segmentStale: true }))
    const calls = captureStreams()

    const wrapper = await mountCard()
    expect(regenerateButton(wrapper)?.attributes('disabled')).toBeDefined()

    calls[0].handlers.onDelta?.('新的叙事。')
    calls[0].handlers.onDone?.(recap({ narrative: '新的叙事（服务端定稿）。', generatedAt: new Date('2026-08-19T12:00:00+08:00') as unknown as Date }))
    calls[0].finish()
    await flushPromises()

    expect(wrapper.findAll('p').map(p => p.text())).toEqual(['新的叙事（服务端定稿）。'])
    expect(regenerateButton(wrapper)?.attributes('disabled')).toBeUndefined()
  })
})

describe('RecapCard 推理透传（ADR-042 §9）', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    captureStreams()
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap())
  })

  it('推理增量滚动显示在限高的滚动容器里（14000 字符不能把卡片撑出屏幕）', async () => {
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('先看 digest：')
    calls[0].handlers.onThinking?.('上午都在 vscode。')
    await flushPromises()

    const panel = thinkingPanel(wrapper)
    expect(panel.exists()).toBe(true)
    expect(panel.text()).toBe('先看 digest：上午都在 vscode。')
    expect(wrapper.text()).toContain('正在思考…')
    // 滚动容器是硬要求：限高 + 自己滚
    expect(panel.classes()).toContain('overflow-y-auto')
    expect(panel.classes().some(c => c.startsWith('max-h-'))).toBe(true)
  })

  it('新推理到达时滚到底（等 nextTick 后 scrollHeight 才是新的）', async () => {
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('第一段推理')
    await flushPromises()
    const metrics = stubScrollMetrics(thinkingPanel(wrapper).element, { scrollHeight: 900, clientHeight: 144 })

    calls[0].handlers.onThinking?.('第二段推理')
    await flushPromises()

    expect(metrics.scrollTop).toBe(900)
  })

  it('用户手动上翻后不再强行拉回底部（抢走滚动位置比不自动滚更烦人）', async () => {
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('第一段推理')
    await flushPromises()
    const panel = thinkingPanel(wrapper)
    const metrics = stubScrollMetrics(panel.element, { scrollHeight: 900, clientHeight: 144 })

    metrics.scrollTop = 100 // 用户翻到上面去读前面的推理
    await panel.trigger('scroll')
    calls[0].handlers.onThinking?.('第二段推理')
    await flushPromises()

    expect(metrics.scrollTop).toBe(100)
  })

  it('用户翻回底部后恢复自动滚底', async () => {
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('第一段推理')
    await flushPromises()
    const panel = thinkingPanel(wrapper)
    const metrics = stubScrollMetrics(panel.element, { scrollHeight: 900, clientHeight: 144 })

    metrics.scrollTop = 100
    await panel.trigger('scroll')
    metrics.scrollTop = 756 // 贴回底部（900 - 144）
    await panel.trigger('scroll')
    calls[0].handlers.onThinking?.('第二段推理')
    await flushPromises()

    expect(metrics.scrollTop).toBe(900)
  })

  it('首个正文 delta 到达后思考面板让位给叙事', async () => {
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('结构定了：上午打磨代码。')
    await flushPromises()
    expect(thinkingPanel(wrapper).exists()).toBe(true)

    calls[0].handlers.onDelta?.('上午你在写代码。')
    await flushPromises()

    expect(thinkingPanel(wrapper).exists()).toBe(false)
    expect(wrapper.findAll('p').map(p => p.text())).toEqual(['上午你在写代码。'])
    expect(wrapper.text()).not.toContain('结构定了')
  })

  it('切日期清空推理：新一天的等待不该显示上一天的思考', async () => {
    vi.mocked(fetchDailyRecap).mockImplementation(async ({ window }) => recap({ date: window.localDate }))
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('上一天的推理')
    await flushPromises()

    await wrapper.setProps({ calendarContext: context('2026-08-18') })
    await flushPromises()

    expect(thinkingPanel(wrapper).exists()).toBe(false)
    expect(wrapper.text()).not.toContain('上一天的推理')
    expect(wrapper.text()).toContain('正在回忆这一天…')
  })

  it('生成失败清空推理：留着一屏思考只是噪音', async () => {
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onThinking?.('想了半天的推理')
    await flushPromises()

    calls[0].handlers.onError?.('上游模型 90 秒未吐出首个 token')
    calls[0].finish()
    await flushPromises()

    expect(thinkingPanel(wrapper).exists()).toBe(false)
    expect(wrapper.text()).not.toContain('想了半天的推理')
    expect(wrapper.text()).toContain('上游模型 90 秒未吐出首个 token')
  })
})

describe('RecapCard 中止与失败', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    captureStreams()
  })

  it('切日期后 abort 旧流，迟到的 delta 不再写进卡片', async () => {
    vi.mocked(fetchDailyRecap).mockImplementation(async ({ window }) =>
      window.localDate === '2026-08-19'
        ? recap()
        : recap({ date: window.localDate, narrative: '另一天的叙事。' }))
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onDelta?.('这一天的第一句。')
    await flushPromises()
    expect(wrapper.text()).toContain('这一天的第一句。')

    await wrapper.setProps({ calendarContext: context('2026-08-18') })
    await flushPromises()

    expect(calls[0].signal?.aborted).toBe(true)
    calls[0].handlers.onDelta?.('迟到的旧文本。') // 真实 wrapper 不会再吐，卡片也必须自己防
    calls[0].finish()
    await flushPromises()

    expect(wrapper.text()).not.toContain('这一天的第一句。')
    expect(wrapper.text()).not.toContain('迟到的旧文本。')
    expect(wrapper.text()).toContain('另一天的叙事。')
  })

  it('同一规范窗口进入新的 refresh generation 后中断旧流并隔离迟到事件', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap())
    const calls = captureStreams()

    const wrapper = await mountCard()
    expect(calls).toHaveLength(1)

    await wrapper.setProps({ calendarContext: contextWithIdentity('2026-08-19', 'next-refresh') })
    await flushPromises()

    expect(calls[0].signal?.aborted).toBe(true)
    expect(calls).toHaveLength(2)
    calls[0].handlers.onDone?.(recap({ narrative: '迟到的旧 generation。' }))
    calls[0].finish()
    calls[1].handlers.onDone?.(recap({ narrative: '当前 generation。' }))
    calls[1].finish()
    await flushPromises()
    expect(wrapper.text()).toContain('当前 generation。')
    expect(wrapper.text()).not.toContain('迟到的旧 generation。')
  })

  it('普通读取原样显示稳定的 calendar mismatch 诊断', async () => {
    vi.mocked(fetchDailyRecap).mockRejectedValue({
      kind: 'calendar',
      code: 'calendar_rules_mismatch',
      message: 'Browser 与 Analytics TZDB 不一致，请更新滞后的运行时。',
    })

    const wrapper = await mountCard()

    expect(wrapper.text()).toContain('Browser 与 Analytics TZDB 不一致，请更新滞后的运行时。')
    expect(wrapper.text()).not.toContain('数据解析失败')
  })

  it('切换 Calendar Context 后迟到的普通读取不能覆盖新窗口', async () => {
    let resolveOld!: (value: DailyRecapResponse) => void
    vi.mocked(fetchDailyRecap).mockImplementation(({ window }) =>
      window.localDate === '2026-08-19'
        ? new Promise(resolve => { resolveOld = resolve })
        : Promise.resolve(recap({ date: window.localDate, narrative: '新窗口叙事。' })))

    const wrapper = await mountCard()
    await wrapper.setProps({ calendarContext: context('2026-08-18') })
    await flushPromises()
    expect(wrapper.text()).toContain('新窗口叙事。')

    resolveOld(recap({ narrative: '迟到的旧窗口叙事。' }))
    await flushPromises()

    expect(wrapper.text()).toContain('新窗口叙事。')
    expect(wrapper.text()).not.toContain('迟到的旧窗口叙事。')
  })

  it('卸载时 abort 在途的生成（连接的寿命就是这次生成的寿命）', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap())
    const calls = captureStreams()

    const wrapper = await mountCard()
    wrapper.unmount()

    expect(calls[0].signal?.aborted).toBe(true)
  })

  it('流内 error 后保留上次成功的叙事，只把原因挂在角上', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '上次成功的叙事。', segmentStale: true }))
    const calls = captureStreams()

    const wrapper = await mountCard()
    calls[0].handlers.onDelta?.('写了一半的新叙事')
    await flushPromises()
    expect(wrapper.text()).toContain('写了一半的新叙事')

    calls[0].handlers.onError?.('上游模型 90 秒未吐出首个 token')
    calls[0].finish()
    await flushPromises()

    expect(wrapper.findAll('p').map(p => p.text())).toEqual(['上次成功的叙事。'])
    expect(wrapper.text()).toContain('上游模型 90 秒未吐出首个 token')
    expect(wrapper.text()).not.toContain('写了一半的新叙事')
  })

  it('撞上并发锁（409）时显示服务端给的可读原因', async () => {
    vi.mocked(fetchDailyRecap).mockResolvedValue(recap({ narrative: '上次成功的叙事。', segmentStale: true }))
    vi.mocked(recapGenerationErrorMessage).mockReturnValue('这一天正在生成中')
    vi.mocked(streamDailyRecapGeneration).mockRejectedValue(
      Object.assign(new Error('conflict'), { isApiException: true, status: 409 }),
    )

    const wrapper = await mountCard()

    expect(wrapper.text()).toContain('这一天正在生成中')
    expect(wrapper.findAll('p').map(p => p.text())).toEqual(['上次成功的叙事。'])
  })
})
