import { describe, expect, it } from 'vitest'
import {
  applyEvent,
  browserPayloadOf,
  emptyState,
  flush,
  type FoldDeps,
} from '../src/fold'
import { activityKeyOf } from '../src/normalize'
import { createSegmentSdk, ROTATE_AFTER_MS } from '../src/sdk/segments'

function makeDeps(): FoldDeps {
  let n = 0
  return {
    segments: createSegmentSdk({ source: 'browser', payloadOf: browserPayloadOf, newId: () => `id-${++n}` }),
    activityKeyOf,
  }
}

const T0 = Date.UTC(2026, 6, 5, 12, 0, 0)

function activated(windowId: number, url: string, at: number, title = 'page') {
  return { kind: 'activated' as const, windowId, url, title, at }
}

describe('applyEvent', () => {
  it('YouTube 切换视频封口旧段并保留各自原始 URL', () => {
    const deps = makeDeps()
    const firstUrl = 'https://www.youtube.com/watch?v=aaa&utm_source=x#t=10'
    const secondUrl = 'https://www.youtube.com/watch?v=bbb&utm_source=y'
    let r = applyEvent(emptyState(), activated(1, firstUrl, T0, 'Video A'), deps)
    r = applyEvent(r.state, activated(1, secondUrl, T0 + 5000, 'Video B'), deps)

    expect(r.out).toHaveLength(1)
    expect(r.out[0]).toMatchObject({
      id: 'id-1', activityKey: 'https://www.youtube.com/watch?v=aaa', title: 'Video A',
      isFinal: true, attributes: { url: firstUrl },
    })
    expect(flush(r.state, T0 + 10_000, deps).out[0]).toMatchObject({
      id: 'id-2', activityKey: 'https://www.youtube.com/watch?v=bbb', title: 'Video B',
      attributes: { url: secondUrl },
    })
  })

  it('同一视频仅追踪参数和锚点变化继续原段', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://www.youtube.com/watch?v=aaa&utm_source=x', T0), deps)
    r = applyEvent(r.state, activated(1, 'https://www.youtube.com/watch?t=30&v=aaa#details', T0 + 1000), deps)

    expect(r.out).toHaveLength(0)
    expect(r.state.open[1]).toMatchObject({ id: 'id-1', activityKey: 'https://www.youtube.com/watch?v=aaa' })
  })

  it('首个激活开启活动，不立即产出快照', () => {
    const deps = makeDeps()
    const { state, out } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    expect(out).toHaveLength(0)
    expect(state.open[1]).toMatchObject({ id: 'id-1', activityKey: 'https://a.com/x', startTime: T0 })
  })

  it('同一 activityKey（query 变化/标题变化）不切段，只更新展示字段', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x?utm=1', T0, 'old'), deps)
    r = applyEvent(r.state, activated(1, 'https://a.com/x?utm=2', T0 + 1000, 'new'), deps)
    expect(r.out).toHaveLength(0)
    expect(r.state.open[1].id).toBe('id-1') // 段身份未变
    expect(r.state.open[1].title).toBe('new')
    expect(r.state.open[1].url).toBe('https://a.com/x?utm=2')
  })

  it('activityKey 变化：封口旧段、开启新段（新 Id）', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    r = applyEvent(r.state, activated(1, 'https://b.com/y', T0 + 5000), deps)

    expect(r.out).toHaveLength(1)
    expect(r.out[0]).toMatchObject({
      id: 'id-1',
      source: 'browser',
      activityKey: 'https://a.com/x',
        startTime: new Date(T0).toISOString(),
      endTime: new Date(T0 + 5000).toISOString(),
      isFinal: true,
    })
    expect(r.state.open[1]).toMatchObject({ id: 'id-2', activityKey: 'https://b.com/y' })
  })

  it('多窗口各自持有活动，互不干扰（windowId 进 attributes）', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    r = applyEvent(r.state, activated(2, 'https://b.com/y', T0 + 100), deps)
    expect(r.out).toHaveLength(0)
    expect(Object.keys(r.state.open)).toHaveLength(2)

    const flushed = flush(r.state, T0 + 60_000, deps)
    expect(flushed.out).toHaveLength(2)
    const byWindow = new Map(flushed.out.map((s) => [s.attributes.windowId, s]))
    expect(byWindow.get(1)?.activityKey).toBe('https://a.com/x')
    expect(byWindow.get(2)?.activityKey).toBe('https://b.com/y')
  })

  it('窗口关闭封口该窗口的活动', () => {
    const deps = makeDeps()
    let r = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    const closed = applyEvent(r.state, { kind: 'windowClosed', windowId: 1, at: T0 + 3000 }, deps)
    expect(closed.out).toHaveLength(1)
    expect(closed.out[0].endTime).toBe(new Date(T0 + 3000).toISOString())
    expect(closed.out[0].isFinal).toBe(true)
    expect(closed.state.open[1]).toBeUndefined()
  })

  it('关闭无活动的窗口是空操作', () => {
    const deps = makeDeps()
    const r = applyEvent(emptyState(), { kind: 'windowClosed', windowId: 9, at: T0 }, deps)
    expect(r.out).toHaveLength(0)
  })

  it('会话恢复后关闭一个窗口只结束该活动，重用窗口号也不续接旧事实', () => {
    const deps = makeDeps()
    const url = 'https://example.com/docs'
    let r = applyEvent(emptyState(), activated(17, url, T0), deps)
    r = applyEvent(r.state, activated(23, url, T0 + 1000), deps)
    const first = flush(r.state, T0 + 30_000, deps)
    // 现有 chrome.storage.session 的形状直接恢复，不需要迁移或第二份对象登记。
    const restored = JSON.parse(JSON.stringify(first.state))
    const closed = applyEvent(restored, { kind: 'windowClosed', windowId: 17, at: T0 + 40_000 }, deps)
    expect(Object.keys(closed.state.open)).toEqual(['23'])
    expect(closed.out).toMatchObject([{ id: 'id-1', isFinal: true, attributes: { windowId: 17 } }])

    const reopened = applyEvent(closed.state, activated(17, url, T0 + 50_000), deps)
    const next = flush(reopened.state, T0 + 60_000, deps)
    expect(next.out.find(s => s.attributes.windowId === 17)).toMatchObject({
      id: 'id-3', startTime: new Date(T0 + 50_000).toISOString(), isFinal: false,
    })
    expect(next.out.find(s => s.attributes.windowId === 23)).toMatchObject({
      id: 'id-2', startTime: new Date(T0 + 1000).toISOString(), isFinal: false,
    })
    expect(restored).toEqual(first.state) // 旧会话快照未被运行操作修改。
  })
})

describe('flush（ADR-018 稳定 Id 快照）', () => {
  it('连续两次 flush：同一 Id，EndTime 单调生长', () => {
    const deps = makeDeps()
    const { state } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)

    const f1 = flush(state, T0 + 30_000, deps)
    const f2 = flush(f1.state, T0 + 60_000, deps)

    expect(f1.out[0].id).toBe('id-1')
    expect(f2.out[0].id).toBe('id-1')
    expect(f1.out[0].startTime).toBe(f2.out[0].startTime)
    expect(f1.out[0].isFinal).toBe(false)
    expect(new Date(f2.out[0].endTime).getTime()).toBeGreaterThan(new Date(f1.out[0].endTime).getTime())
  })

  it('快照携带完整原始 URL、domain 与 site（无损原则 + 深度表 v2 运输槽）', () => {
    const deps = makeDeps()
    const { state } = applyEvent(
      emptyState(),
      activated(1, 'https://www.youtube.com/watch?v=abc#t=10', T0),
      deps,
    )
    const f = flush(state, T0 + 1000, deps)
    expect(f.out[0].attributes).toEqual({
      url: 'https://www.youtube.com/watch?v=abc#t=10',
      domain: 'www.youtube.com',
      site: 'youtube.com',
      windowId: 1,
    })
  })

  it('段只携带观测内容，App 身份属于 Stream', () => {
    const deps = makeDeps()
    const { state } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)
    const [snapshot] = flush(state, T0 + 1000, deps).out

    expect(snapshot).not.toHaveProperty('appHint')
    expect(snapshot).toMatchObject({
      source: 'browser',
      activityKey: 'https://a.com/x',
      title: 'page',
    })
  })

  it('空状态 flush 无产出且状态不变', () => {
    const deps = makeDeps()
    const s = emptyState()
    const f = flush(s, T0, deps)
    expect(f.out).toHaveLength(0)
    expect(f.state).toBe(s)
  })

  it('超长活动轮换：旧段封口、同活动换新 Id 续记（防超服务端 MaxDuration 被丢）', () => {
    const deps = makeDeps()
    const { state } = applyEvent(emptyState(), activated(1, 'https://a.com/x', T0), deps)

    const rotateAt = T0 + ROTATE_AFTER_MS
    const f = flush(state, rotateAt, deps)

    expect(f.out).toHaveLength(1)
    expect(f.out[0].id).toBe('id-1') // 旧段最终快照
    expect(f.out[0].endTime).toBe(new Date(rotateAt).toISOString())
    expect(f.out[0].isFinal).toBe(true)

    const rotated = f.state.open[1]
    expect(rotated.id).toBe('id-2') // 新 Id 从 now 续记
    expect(rotated.startTime).toBe(rotateAt)
    expect(rotated.activityKey).toBe('https://a.com/x') // 活动身份不变

    // 轮换后的下一次 flush 用新 Id
    const f2 = flush(f.state, rotateAt + 30_000, deps)
    expect(f2.out[0].id).toBe('id-2')
    expect(f2.out[0].isFinal).toBe(false)
  })
})
