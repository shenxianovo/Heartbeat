import { describe, expect, it } from 'vitest'
import scenarios from '../../../shared/calendar-window-golden-scenarios.json'
import {
  CalendarContextError,
  resolveCalendarContext,
} from './localCalendarWindow'

describe('resolveCalendarContext', () => {
  for (const scenario of scenarios) {
    it(`resolves the shared ${scenario.name} scenario`, () => {
      if (scenario.error) {
        expect(() => resolveCalendarContext(scenario.localDate, {
          timeZone: scenario.timeZone,
          now: '2026-08-29T00:00:00Z',
          correlationIdentity: () => 'refresh-1',
        })).toThrowError(expect.objectContaining({ code: scenario.error }))
        return
      }

      const context = resolveCalendarContext(scenario.localDate, {
        timeZone: scenario.timeZone,
        now: '2026-08-29T00:00:00Z',
        correlationIdentity: () => 'refresh-1',
      })

      expect(context.day).toEqual({
        version: 1,
        kind: 'day',
        localDate: scenario.localDate,
        timeZone: scenario.timeZone,
        start: scenario.start,
        endExclusive: scenario.endExclusive,
      })
      expect((Date.parse(context.day.endExclusive) - Date.parse(context.day.start)) / 3_600_000)
        .toBe(scenario.durationHours)
      expect(context.week).toEqual({
        version: 1,
        kind: 'week',
        localDate: scenario.localDate,
        timeZone: scenario.timeZone,
        start: scenario.weekStart,
        endExclusive: scenario.weekEndExclusive,
      })
      expect((Date.parse(context.week.endExclusive) - Date.parse(context.week.start)) / 3_600_000)
        .toBe(scenario.weekDurationHours)
      expect(context.correlationIdentity).toBe('refresh-1')
      expect(context.displayLabel).toContain(scenario.localDate)
      expect(context.displayLabel).toContain(scenario.timeZone)
      expect(Object.isFrozen(context)).toBe(true)
      expect(Object.isFrozen(context.day)).toBe(true)
    })
  }

  it('rejects non-canonical and impossible local dates with a stable code', () => {
    for (const localDate of ['2026-8-29', '2026-02-29', '0000-01-01', 'not-a-date']) {
      expect(() => resolveCalendarContext(localDate, {
        timeZone: 'Asia/Shanghai',
        now: '2026-08-29T00:00:00Z',
      })).toThrowError(expect.objectContaining({ code: 'invalid_local_date' }))
    }
  })

  it('rejects unsupported timezones with a stable code', () => {
    expect(() => resolveCalendarContext('2026-08-29', {
      timeZone: 'Mars/Olympus_Mons',
      now: '2026-08-29T00:00:00Z',
    })).toThrowError(expect.objectContaining({ code: 'unsupported_timezone' }))
  })

  it('derives today in the captured timezone and gives every refresh its own identity', () => {
    const identities = ['refresh-1', 'refresh-2']
    const options = {
      timeZone: 'America/Los_Angeles',
      now: '2026-08-29T01:00:00Z',
      correlationIdentity: () => identities.shift()!,
    }

    const first = resolveCalendarContext('2026-08-28', options)
    const second = resolveCalendarContext('2026-08-28', options)

    expect(first.isToday).toBe(true)
    expect(first.correlationIdentity).not.toBe(second.correlationIdentity)
  })

  it('exposes diagnostic errors without leaking Temporal exceptions', () => {
    try {
      resolveCalendarContext('2026-08-29', { timeZone: 'Bad/Zone' })
      expect.fail('expected CalendarContextError')
    } catch (error) {
      expect(error).toBeInstanceOf(CalendarContextError)
      expect((error as CalendarContextError).message).toContain('Bad/Zone')
    }
  })
})
