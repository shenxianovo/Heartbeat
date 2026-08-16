import { describe, expect, it } from 'vitest'
import { formatExactLocalDateTime, formatLastSeen, latestDate } from './lastSeen'

describe('last-seen presentation', () => {
  const now = new Date(2026, 7, 16, 15, 0, 0)

  it('uses calendar-aware labels without losing the date', () => {
    expect(formatLastSeen(new Date(2026, 7, 16, 14, 32), now)).toBe('今天 14:32')
    expect(formatLastSeen(new Date(2026, 7, 15, 23, 18), now)).toBe('昨天 23:18')
    expect(formatLastSeen(new Date(2026, 7, 12, 9, 5), now)).toBe('8月12日 09:05')
    expect(formatLastSeen(new Date(2025, 11, 3, 20, 41), now)).toBe('2025年12月3日 20:41')
  })

  it('keeps the exact local timestamp for the tooltip', () => {
    expect(formatExactLocalDateTime(new Date(2025, 11, 3, 20, 41, 27)))
      .toBe('2025-12-03 20:41:27')
  })

  it('selects the most recent valid heartbeat', () => {
    const newest = new Date(2026, 7, 16, 12, 0)
    expect(latestDate([
      new Date(2026, 7, 10, 12, 0),
      null,
      newest,
      new Date(Number.NaN),
    ])).toBe(newest)
  })
})
