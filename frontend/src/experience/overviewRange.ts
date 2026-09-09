import { clampRange, type TimeRange } from './factViews'

export type OverviewDrag = 'start' | 'end' | 'move' | 'select'

export function dragOverview(mode: OverviewDrag, original: TimeRange, bounds: TimeRange, anchor: number, current: number): TimeRange {
  const delta = current - anchor
  if (mode === 'move') return clampRange({ start: original.start + delta, end: original.end + delta }, bounds)
  if (mode === 'start') return { start: Math.max(bounds.start, Math.min(original.start + delta, original.end - 1000)), end: original.end }
  if (mode === 'end') return { start: original.start, end: Math.min(bounds.end, Math.max(original.end + delta, original.start + 1000)) }
  return clampRange({ start: Math.min(anchor, current), end: Math.max(anchor, current) }, bounds)
}
