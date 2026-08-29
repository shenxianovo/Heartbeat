// 时间轴标尺工具：ActivityTimeline 与 AppDetailModal 共用（原各持一份重复实现）。

export function fmtTime(ms: number, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone,
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(ms)
}

export interface Tick {
  at: number
  percent: number
  label: string
}

// Nice intervals: 1m, 2m, 5m, 10m, 15m, 30m, 1h, 2h, 3h, 6h
const NICE_INTERVALS = [
  60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000,
  3_600_000, 7_200_000, 10_800_000, 21_600_000,
]

/** 生成不超过 maxTicks 个、对齐到 nice 间隔整点的刻度（percent 相对 [start, end]）。 */
export function niceTicks(start: number, end: number, maxTicks: number, timeZone: string): Tick[] {
  const range = end - start
  if (range <= 0) return []

  let interval = NICE_INTERVALS[NICE_INTERVALS.length - 1]
  for (const ni of NICE_INTERVALS) {
    if (range / ni <= maxTicks) {
      interval = ni
      break
    }
  }

  const ticks: Tick[] = []
  for (let t = Math.ceil(start / interval) * interval; t <= end; t += interval) {
    ticks.push({ at: t, percent: ((t - start) / range) * 100, label: fmtTime(t, timeZone) })
  }
  return ticks
}
