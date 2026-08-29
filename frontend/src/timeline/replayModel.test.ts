import { describe, it, expect } from 'vitest'
import { envelope, buildTracks as buildTracksInTimeZone, type ReplaySeg } from './replayModel'

const base = new Date(2026, 0, 15, 10, 0, 0).getTime()
const sec = (n: number) => n * 1000
const localTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone

function seg(source: string, startMs: number, endMs: number, opts: Partial<ReplaySeg> = {}): ReplaySeg {
  return { source, start: startMs, end: endMs, label: '', ...opts }
}

function buildTracks(
  segs: ReplaySeg[],
  view: { start: number; end: number },
  timeZone = localTimeZone,
) {
  return buildTracksInTimeZone(segs, view, timeZone)
}

describe('envelope', () => {
  it('空输入 / 零跨度返回 null', () => {
    expect(envelope([])).toBeNull()
    expect(envelope([{ start: base, end: base }])).toBeNull()
  })

  it('pad 取 3% 与 60s 的较大者', () => {
    // 10min 跨度：3% = 18s < 60s → 用 60s 下限
    const short = envelope([{ start: base, end: base + sec(600) }])!
    expect(short.start).toBe(base - sec(60))
    expect(short.end).toBe(base + sec(660))

    // 10h 跨度：3% = 18min > 60s → 用比例
    const long = envelope([{ start: base, end: base + sec(36000) }])!
    expect(long.start).toBe(base - sec(1080))
  })
})

describe('buildTracks', () => {
  const view = { start: base, end: base + sec(1000) }

  it('按 source 分轨，保持相遇顺序', () => {
    const tracks = buildTracks([
      seg('system', base, base + sec(100)),
      seg('browser', base, base + sec(100)),
    ], view)
    expect(tracks.map(t => t.source)).toEqual(['system', 'browser'])
  })

  it('点事件（<1s）isPoint 且宽度 0；普通段有最小宽度 0.4', () => {
    const tracks = buildTracks([
      seg('browser', base + sec(10), base + sec(10) + 500),
      seg('browser', base + sec(600), base + sec(601)),
    ], view)
    const bars = tracks[0].lanes.flatMap(l => l.bars)
    const point = bars.find(b => b.isPoint)!
    expect(point.width).toBe(0)
    const normal = bars.find(b => !b.isPoint)!
    expect(normal.width).toBe(0.4) // 1s/1000s = 0.1% → 抬到下限
  })

  it('有 laneKey 的段按身份分稳定泳道', () => {
    // 两个浏览器窗口时间重叠 → 两条 lane，key 各自持有
    const tracks = buildTracks([
      seg('browser', base, base + sec(300), { laneKey: '1' }),
      seg('browser', base + sec(100), base + sec(400), { laneKey: '2' }),
      seg('browser', base + sec(350), base + sec(500), { laneKey: '1' }),
    ], view)
    const lanes = tracks[0].lanes
    expect(lanes.length).toBe(2)
    expect(lanes.map(l => l.key)).toEqual(['1', '2'])
    expect(lanes[0].bars.length).toBe(2)
  })

  it('无 laneKey 的段贪心装箱：重叠开新 lane，不重叠共用', () => {
    const tracks = buildTracks([
      seg('vscode', base, base + sec(300)),
      seg('vscode', base + sec(100), base + sec(200)), // 与首段重叠 → 新 lane
      seg('vscode', base + sec(300), base + sec(400)), // 首段结束后 → 回填 lane 1
    ], view)
    const lanes = tracks[0].lanes
    expect(lanes.length).toBe(2)
    expect(lanes[0].bars.length).toBe(2)
    expect(lanes[1].bars.length).toBe(1)
    expect(lanes.every(l => l.key === undefined)).toBe(true)
  })

  it('lane 按首段开始时间排序', () => {
    const tracks = buildTracks([
      seg('browser', base + sec(200), base + sec(300), { laneKey: 'late' }),
      seg('browser', base, base + sec(250), { laneKey: 'early' }),
    ], view)
    expect(tracks[0].lanes.map(l => l.key)).toEqual(['early', 'late'])
  })

  it('tooltip：有 label 带内容，无 label 只有时间', () => {
    const tracks = buildTracks([
      seg('system', base, base + sec(60), { label: 'main.cs - VS Code' }),
      seg('system', base + sec(100), base + sec(160)),
    ], view)
    const bars = tracks[0].lanes[0].bars
    expect(bars[0].tooltip).toMatch(/^\d{2}:\d{2} - \d{2}:\d{2}  main\.cs - VS Code$/)
    expect(bars[1].tooltip).toMatch(/^\d{2}:\d{2} - \d{2}:\d{2}$/)
  })

  it('用捕获的 Calendar Context timezone 格式化回放 tooltip', () => {
    const start = Date.parse('2026-11-01T05:00:00Z')
    const tracks = buildTracks([
      seg('system', start, start + 30 * 60_000),
    ], { start, end: start + 60 * 60_000 }, 'America/New_York')

    expect(tracks[0].lanes[0].bars[0].tooltip).toBe('01:00 - 01:30')
  })

  it('按半开视窗裁剪跨窗段，并排除只贴住端点的段', () => {
    const tracks = buildTracks([
      seg('system', base - sec(100), base + sec(1100)),
      seg('system', base - sec(10), base),
      seg('system', base + sec(1000), base + sec(1010)),
    ], view)

    expect(tracks).toHaveLength(1)
    expect(tracks[0].lanes.flatMap(lane => lane.bars)).toEqual([
      expect.objectContaining({ left: 0, width: 100 }),
    ])
  })

  it('零长点在窗口起点可见，在 end-exclusive 不可见', () => {
    const tracks = buildTracks([
      seg('browser', view.start, view.start),
      seg('browser', view.end, view.end),
    ], view)

    const bars = tracks[0].lanes.flatMap(lane => lane.bars)
    expect(bars).toHaveLength(1)
    expect(bars[0]).toEqual(expect.objectContaining({ left: 0, isPoint: true }))
  })
})
