import { describe, it, expect } from 'vitest'
import {
  parseUsage,
  buildRows,
  mergeActivityBursts,
  initialViewBounds,
  buildDayHourBins,
  projectInterval,
  onlineUnionSeconds,
  groupByDevice,
  MERGE_GAP_MS,
  type UsageLike,
} from './timelineModel'
import { AWAY_APP } from '../appLabels'

const base = new Date(2026, 0, 15, 10, 0, 0).getTime()
const sec = (n: number) => n * 1000
const localTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone

function usage(appId: number, startMs: number, endMs: number, appName = `app${appId}`): UsageLike {
  return { appId, appName, startTime: new Date(startMs), endTime: new Date(endMs) }
}

function onDevice(deviceId: number, u: UsageLike): UsageLike {
  return { ...u, deviceId }
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

  it('away 段优先按产品 Key 识别，旧响应回退 appName', () => {
    const parsed = parseUsage([
      usage(1, base, base + sec(10)),
      usage(9, base + sec(20), base + sec(30), AWAY_APP),
      { ...usage(10, base + sec(40), base + sec(50), 'renamed'), appKey: 'away' },
      { ...usage(11, base + sec(60), base + sec(70), AWAY_APP), appKey: 'not-away' },
    ])
    expect(parsed.awayAppIds).toEqual(new Set([9, 10]))
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
    const rows = buildRows(parsed, view, localTimeZone)
    expect(rows.map(r => r.appId)).toEqual([2, 1])
  })

  it('投影钳位到 [0,100]，宽度下限 0.5，label 为时间区间', () => {
    const parsed = parseUsage([
      usage(1, base - sec(50), base + sec(50)),  // 左越界
      usage(2, base + sec(10), base + sec(10) + 100), // 0.1s 极窄段
    ])
    const rows = buildRows(parsed, view, localTimeZone)
    const wide = rows.find(r => r.appId === 1)!.bars[0]
    expect(wide.left).toBe(0)
    expect(wide.width).toBeCloseTo(50)
    const narrow = rows.find(r => r.appId === 2)!.bars[0]
    expect(narrow.width).toBe(0.5)
    expect(wide.label).toMatch(/^\d{2}:\d{2} - \d{2}:\d{2}$/)
  })

  it('用捕获的 Calendar Context timezone 格式化 bar tooltip', () => {
    const start = Date.parse('2026-11-01T05:00:00Z')
    const parsed = parseUsage([usage(1, start, start + 30 * 60_000)])
    const rows = buildRows(parsed, { start, end: start + 60 * 60_000 }, 'America/New_York')

    expect(rows[0].bars[0].label).toBe('01:00 - 01:30')
  })

  it('away 行带 isAway 标记', () => {
    const parsed = parseUsage([usage(9, base, base + sec(10), AWAY_APP)])
    expect(buildRows(parsed, view, localTimeZone)[0].isAway).toBe(true)
  })

  it('倒置视窗返回空', () => {
    const parsed = parseUsage([usage(1, base, base + sec(10))])
    expect(buildRows(parsed, { start: base, end: base }, localTimeZone)).toEqual([])
  })

  it('按半开窗口裁剪，恰好贴住两端的段不进入视图', () => {
    const parsed = parseUsage([
      usage(1, base - sec(10), base),
      usage(2, base + sec(100), base + sec(110)),
      usage(3, base - sec(10), base + sec(10)),
      usage(4, base + sec(90), base + sec(110)),
    ])

    expect(buildRows(parsed, view, localTimeZone).map(row => row.appId)).toEqual([3, 4])
  })
})

describe('Local Calendar Window timeline layout', () => {
  const H = 3_600_000
  const start = Date.parse('2026-03-08T05:00:00Z')

  it.each([23, 24, 25])('lays out a %i-hour day using its exact endpoints', (hours) => {
    const day = { start, end: start + hours * H }
    const bins = buildDayHourBins(day, [], 'America/New_York')

    expect(bins).toHaveLength(hours)
    expect(bins[0].start).toBe(day.start)
    expect(bins[bins.length - 1].end).toBe(day.end)
  })

  it('distinguishes the repeated fall-back hour by instant while keeping its civil label', () => {
    const day = {
      start: Date.parse('2026-11-01T04:00:00Z'),
      end: Date.parse('2026-11-02T05:00:00Z'),
    }
    const bins = buildDayHourBins(day, [], 'America/New_York')

    expect(bins.filter(bin => bin.label === '01:00')).toHaveLength(2)
    expect(new Set(bins.map(bin => bin.start)).size).toBe(25)
  })

  it('projects overlap into the day and excludes intervals touching only an endpoint', () => {
    const day = { start, end: start + 23 * H }

    expect(projectInterval({ start: start - H, end: start + H }, day)).toEqual({
      left: 0,
      width: 100 / 23,
    })
    expect(projectInterval({ start: start - H, end: start }, day)).toBeNull()
    expect(projectInterval({ start: day.end, end: day.end + H }, day)).toBeNull()
    expect(projectInterval({ start: start - H, end: day.end + H }, day)).toEqual({
      left: 0,
      width: 100,
    })
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

describe('onlineUnionSeconds', () => {
  it('单设备连续段 = 求和', () => {
    expect(onlineUnionSeconds([
      usage(1, base, base + sec(30)),
      usage(2, base + sec(60), base + sec(90)),
    ])).toBe(60)
  })

  it('双设备重叠只算一份墙钟（并集 < 求和）', () => {
    const u = [
      onDevice(1, usage(1, base, base + sec(60))),
      onDevice(2, usage(2, base + sec(30), base + sec(90))),
    ]
    expect(onlineUnionSeconds(u)).toBe(90)
  })

  it('完全包含的段不增加时长', () => {
    expect(onlineUnionSeconds([
      onDevice(1, usage(1, base, base + sec(100))),
      onDevice(2, usage(2, base + sec(20), base + sec(50))),
    ])).toBe(100)
  })

  it('away 段不计入在线', () => {
    expect(onlineUnionSeconds([
      usage(1, base, base + sec(30)),
      { ...usage(9, base + sec(30), base + sec(600), 'renamed'), appKey: 'away' },
    ])).toBe(30)
  })

  it('乱序输入、缺字段、零长段都安全', () => {
    expect(onlineUnionSeconds([
      usage(2, base + sec(60), base + sec(90)),
      usage(1, base, base + sec(30)),
      { appId: 3, appName: 'x' },
      usage(4, base + sec(120), base + sec(120)),
    ])).toBe(60)
  })

  it('空输入为 0', () => {
    expect(onlineUnionSeconds([])).toBe(0)
  })

  it('相邻但不重叠的段不缝合(不在就是不在)', () => {
    // 两段间隔 1s：mergeActivityBursts 会缝，并集不缝
    expect(onlineUnionSeconds([
      usage(1, base, base + sec(30)),
      usage(1, base + sec(31), base + sec(61)),
    ])).toBe(60)
  })
})

describe('groupByDevice', () => {
  it('按设备分组，组间按首段开始时间升序', () => {
    const groups = groupByDevice([
      onDevice(2, usage(1, base + sec(100), base + sec(200))),
      onDevice(1, usage(2, base, base + sec(50))),
      onDevice(2, usage(3, base + sec(300), base + sec(400))),
    ])
    expect([...groups.keys()]).toEqual([1, 2])
    expect(groups.get(2)!.length).toBe(2)
  })

  it('缺 deviceId 归入 0 组', () => {
    const groups = groupByDevice([usage(1, base, base + sec(10))])
    expect([...groups.keys()]).toEqual([0])
  })

  it('空输入返回空 Map', () => {
    expect(groupByDevice([]).size).toBe(0)
  })
})

describe('initialViewBounds', () => {
  const now = base + sec(500)
  const H = 3_600_000
  const day = { start: base - 12 * H, end: base + 11 * H }

  it('今天：now ±1h', () => {
    expect(initialViewBounds(day, true, [], now)).toEqual({
      start: now - H,
      end: now + H,
    })
  })

  it('历史日：首个事件 ±1h', () => {
    const b = initialViewBounds(day, false, [usage(1, base, base + sec(10))], now)
    expect(b).toEqual({ start: base - H, end: base + H })
  })

  it('历史日无数据：落在精确日窗的中间两小时', () => {
    expect(initialViewBounds(day, false, [], now)).toEqual({
      start: (day.start + day.end) / 2 - H,
      end: (day.start + day.end) / 2 + H,
    })
  })

  it('今天和首个事件靠近日窗边界时，视窗保持在 23 小时日内', () => {
    const springStart = Date.parse('2026-03-08T05:00:00Z')
    const springDay = { start: springStart, end: springStart + 23 * H }

    expect(initialViewBounds(springDay, true, [], springStart + 30 * 60_000)).toEqual({
      start: springStart,
      end: springStart + 2 * H,
    })
    expect(initialViewBounds(
      springDay,
      false,
      [usage(1, springDay.end - 30 * 60_000, springDay.end)],
      now,
    )).toEqual({
      start: springDay.end - 2 * H,
      end: springDay.end,
    })
  })
})
