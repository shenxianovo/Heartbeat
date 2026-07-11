import { describe, it, expect } from 'vitest'
import {
  parseUsage,
  buildRows,
  mergeActivityBursts,
  initialViewBounds,
  MERGE_GAP_MS,
  type UsageLike,
} from './timelineModel'
import { AWAY_APP } from '../appLabels'

const base = new Date(2026, 0, 15, 10, 0, 0).getTime()
const sec = (n: number) => n * 1000

function usage(appId: number, startMs: number, endMs: number, appName = `app${appId}`): UsageLike {
  return { appId, appName, startTime: new Date(startMs), endTime: new Date(endMs) }
}

describe('parseUsage', () => {
  it('缺字段的记录跳过', () => {
    const parsed = parseUsage([
      { appId: 1, appName: 'a' }, // 无时间
      { appName: 'b', startTime: new Date(base), endTime: new Date(base + sec(10)) }, // 无 appId
    ])
    expect(parsed.byApp.size).toBe(0)
  })

  it('≤2s 缝隙缝合，>2s 保持分段（乱序输入先排序）', () => {
    const parsed = parseUsage([
      usage(1, base + sec(70), base + sec(80)), // 与前段隔 10s，不缝
      usage(1, base, base + sec(30)),
      usage(1, base + sec(30) + MERGE_GAP_MS, base + sec(60)), // 恰好 2s 缝，缝合
    ])
    const segs = parsed.byApp.get(1)!
    expect(segs).toEqual([
      { start: base, end: base + sec(60) },
      { start: base + sec(70), end: base + sec(80) },
    ])
  })

  it('away 段按 appName 识别进 awayAppIds', () => {
    const parsed = parseUsage([
      usage(1, base, base + sec(10)),
      usage(9, base + sec(20), base + sec(30), AWAY_APP),
    ])
    expect(parsed.awayAppIds).toEqual(new Set([9]))
  })
})

describe('buildRows', () => {
  const view = { start: base, end: base + sec(100) }

  it('按可见时长降序，视窗外的 App 不出现', () => {
    const parsed = parseUsage([
      usage(1, base, base + sec(10)),                       // 可见 10s
      usage(2, base + sec(20), base + sec(60)),             // 可见 40s
      usage(3, base + sec(200), base + sec(300)),           // 视窗外
    ])
    const rows = buildRows(parsed, view)
    expect(rows.map(r => r.appId)).toEqual([2, 1])
  })

  it('投影钳位到 [0,100]，宽度下限 0.5，label 为时间区间', () => {
    const parsed = parseUsage([
      usage(1, base - sec(50), base + sec(50)),  // 左越界
      usage(2, base + sec(10), base + sec(10) + 100), // 0.1s 极窄段
    ])
    const rows = buildRows(parsed, view)
    const wide = rows.find(r => r.appId === 1)!.bars[0]
    expect(wide.left).toBe(0)
    expect(wide.width).toBeCloseTo(50)
    const narrow = rows.find(r => r.appId === 2)!.bars[0]
    expect(narrow.width).toBe(0.5)
    expect(wide.label).toMatch(/^\d{2}:\d{2} - \d{2}:\d{2}$/)
  })

  it('away 行带 isAway 标记', () => {
    const parsed = parseUsage([usage(9, base, base + sec(10), AWAY_APP)])
    expect(buildRows(parsed, view)[0].isAway).toBe(true)
  })

  it('倒置视窗返回空', () => {
    const parsed = parseUsage([usage(1, base, base + sec(10))])
    expect(buildRows(parsed, { start: base, end: base })).toEqual([])
  })
})

describe('mergeActivityBursts', () => {
  it('跨 App 合并 ≤1min 间断，away 不算活跃', () => {
    const parsed = parseUsage([
      usage(1, base, base + sec(30)),
      usage(2, base + sec(60), base + sec(90)),     // 与前段隔 30s → 缝合
      usage(3, base + sec(300), base + sec(330)),   // 隔 3.5min → 新爆发
      usage(9, base + sec(90), base + sec(300), AWAY_APP), // away 不参与
    ])
    expect(mergeActivityBursts(parsed)).toEqual([
      { start: base, end: base + sec(90) },
      { start: base + sec(300), end: base + sec(330) },
    ])
  })

  it('空数据返回空', () => {
    expect(mergeActivityBursts(parseUsage([]))).toEqual([])
  })
})

describe('initialViewBounds', () => {
  const now = base + sec(500)
  const H = 3_600_000

  it('今天：now ±1h', () => {
    expect(initialViewBounds('2026-01-15', true, [], now)).toEqual({
      start: now - H,
      end: now + H,
    })
  })

  it('历史日：首个事件 ±1h', () => {
    const b = initialViewBounds('2026-01-14', false, [usage(1, base, base + sec(10))], now)
    expect(b).toEqual({ start: base - H, end: base + H })
  })

  it('历史日无数据：落在当日 11:00-13:00', () => {
    const dayBase = new Date('2026-01-14').getTime()
    expect(initialViewBounds('2026-01-14', false, [], now)).toEqual({
      start: dayBase + 11 * H,
      end: dayBase + 13 * H,
    })
  })
})
