import { describe, expect, it } from 'vitest'
import { overlapMs, upgradeBreakdown, type PluginSeg, type SystemSeg } from './labelUpgrade'

const T = 1_000_000_000_000 // 任意基准时刻

function sys(start: number, end: number, title = 'win title'): SystemSeg {
  return { contextKey: 'machine/app', start: T + start, end: T + end, appName: 'msedge', title }
}

function page(start: number, end: number, key: string, title = 'page', url?: string): PluginSeg {
  return { contextKey: 'machine/app', start: T + start, end: T + end, identityKey: key, title, url: url ?? key }
}

/** 直通 fallback：primary = 原始标题。 */
const passthrough = (_app: string | undefined, t: string | null | undefined) => ({ primary: t ?? '' })

describe('overlapMs', () => {
  it('相交区间返回重叠长度，无交集返回 0', () => {
    expect(overlapMs({ start: 0, end: 10 }, { start: 5, end: 20 })).toBe(5)
    expect(overlapMs({ start: 0, end: 10 }, { start: 10, end: 20 })).toBe(0)
  })
})

describe('upgradeBreakdown', () => {
  it('有重叠插件段：标签升级为页面标题/URL，时长取 system 段', () => {
    const rows = upgradeBreakdown(
      [sys(0, 60_000, '补药视奸我 和另外 2 个页面')],
      [page(0, 55_000, 'https://github.com/x', 'GitHub PR', 'https://github.com/x?tab=1')],
      passthrough,
    )
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({
      title: 'GitHub PR',
      secondary: 'https://github.com/x?tab=1',
      upgraded: true,
      totalSeconds: 60, // system 段时长，不是插件段的 55s
      count: 1,
    })
  })

  it('多个插件段重叠时取重叠占比最大者', () => {
    const rows = upgradeBreakdown(
      [sys(0, 60_000)],
      [
        page(0, 10_000, 'https://a.com/x', 'A'), // 重叠 10s
        page(10_000, 60_000, 'https://b.com/y', 'B'), // 重叠 50s
      ],
      passthrough,
    )
    expect(rows).toHaveLength(1)
    expect(rows[0].title).toBe('B')
  })

  it('同一页面多次停留按 identityKey 合并', () => {
    const rows = upgradeBreakdown(
      [sys(0, 30_000), sys(60_000, 90_000)],
      [page(0, 30_000, 'https://a.com/x', 'A'), page(60_000, 90_000, 'https://a.com/x', 'A')],
      passthrough,
    )
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ totalSeconds: 60, count: 2 })
  })

  it('fallback 按时间窗口判定：无重叠的 system 段走窗口标题，与升级行并存', () => {
    const rows = upgradeBreakdown(
      [sys(0, 30_000, 'old era title'), sys(100_000, 130_000, 'covered')],
      [page(100_000, 130_000, 'https://a.com/x', 'A')],
      passthrough,
    )
    expect(rows).toHaveLength(2)
    const upgraded = rows.find((r) => r.upgraded)
    const fallbackRow = rows.find((r) => !r.upgraded)
    expect(upgraded?.title).toBe('A')
    expect(fallbackRow?.title).toBe('old era title')
  })

  it('零长度插件段（点事件）不参与升级', () => {
    const rows = upgradeBreakdown(
      [sys(0, 30_000, 'win')],
      [page(10_000, 10_000, 'https://a.com/pt', 'point')],
      passthrough,
    )
    expect(rows[0].upgraded).toBe(false)
  })

  it('无插件段时行为与纯 fallback 聚合一致', () => {
    const rows = upgradeBreakdown([sys(0, 10_000, 't1'), sys(20_000, 30_000, 't1')], [], passthrough)
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ title: 't1', totalSeconds: 20, count: 2, upgraded: false })
  })

  it('结果按时长降序', () => {
    const rows = upgradeBreakdown(
      [sys(0, 10_000, 'short'), sys(20_000, 80_000, 'long')],
      [],
      passthrough,
    )
    expect(rows[0].title).toBe('long')
  })
})
