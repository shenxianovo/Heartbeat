import { describe, it, expect } from 'vitest'
import { fmtTime, niceTicks } from './timeScale'

// 用本地时间构造，避免断言依赖运行环境时区
const at = (h: number, m: number, s = 0) => new Date(2026, 0, 15, h, m, s).getTime()

describe('fmtTime', () => {
  it('补零到 HH:MM', () => {
    expect(fmtTime(at(9, 5))).toBe('09:05')
    expect(fmtTime(at(23, 59))).toBe('23:59')
  })
})

describe('niceTicks', () => {
  it('空/倒置区间返回空', () => {
    expect(niceTicks(1000, 1000, 10)).toEqual([])
    expect(niceTicks(2000, 1000, 10)).toEqual([])
  })

  it('选满足 maxTicks 的最小 nice 间隔', () => {
    // 30 分钟 / maxTicks=10 → 5m 间隔（30/1m=30、/2m=15 超，/5m=6 ≤10）
    const ticks = niceTicks(at(12, 0), at(12, 30), 10)
    expect(ticks.map(t => t.label)).toEqual([
      '12:00', '12:05', '12:10', '12:15', '12:20', '12:25', '12:30',
    ])
  })

  it('首刻度对齐到间隔整点', () => {
    // 12:03 起视窗，5m 间隔 → 首刻度 12:05
    const ticks = niceTicks(at(12, 3), at(12, 33), 10)
    expect(ticks[0].label).toBe('12:05')
    expect(ticks[0].percent).toBeCloseTo(((2 * 60_000) / (30 * 60_000)) * 100)
  })

  it('percent 落在 [0,100] 且单调递增', () => {
    const ticks = niceTicks(at(8, 0), at(20, 0), 8)
    for (const t of ticks) {
      expect(t.percent).toBeGreaterThanOrEqual(0)
      expect(t.percent).toBeLessThanOrEqual(100)
    }
    for (let i = 1; i < ticks.length; i++) {
      expect(ticks[i].percent).toBeGreaterThan(ticks[i - 1].percent)
    }
  })

  it('超长区间封顶到最大间隔（6h），刻度数可超 maxTicks', () => {
    // 3 天 / maxTicks=2：无 nice 间隔满足，回落到 6h。起点取 6h 对齐格避免时区影响计数。
    const start = Math.ceil(at(0, 0) / 21_600_000) * 21_600_000
    const ticks = niceTicks(start, start + 3 * 24 * 3_600_000, 2)
    expect(ticks.length).toBe(13) // 3d/6h + 1
  })
})
