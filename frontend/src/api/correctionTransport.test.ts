// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { proposeCorrection, toApiError } from './index'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: 'tok-owner' },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

const fetchMock = vi.fn()
const dayWindow: CalendarWindowEnvelope<'day'> = {
  version: 1,
  kind: 'day',
  localDate: '2026-03-08',
  timeZone: 'America/New_York',
  start: '2026-03-08T05:00:00Z',
  endExclusive: '2026-03-09T04:00:00Z',
}

describe('Recap correction Local Calendar Window transport', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({ explanation: 'ok', operations: [] }),
      json: async () => ({ explanation: 'ok', operations: [] }),
    } as Response)
  })

  it('uses the generated client to send the complete day envelope and no fixed-offset date', async () => {
    await proposeCorrection({ window: dayWindow, correction: '那天其实是在做调研' })

    const [rawUrl, init] = fetchMock.mock.calls[0]
    const url = new URL(rawUrl, 'https://heartbeat.test')
    expect(url.pathname).toBe('/api/v1/knowledge/corrections/propose')
    expect(Object.fromEntries(url.searchParams)).toEqual({
      Version: '1',
      Kind: 'day',
      LocalDate: '2026-03-08',
      TimeZone: 'America/New_York',
      Start: '2026-03-08T05:00:00.000Z',
      EndExclusive: '2026-03-09T04:00:00.000Z',
    })
    expect(JSON.parse(init.body)).toEqual({ correction: '那天其实是在做调研' })
  })

  it('preserves a diagnostic calendar mismatch from Analytics', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 400,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({
        code: 'calendar_rules_mismatch',
        message: 'Browser and Analytics TZDB disagree.',
      }),
    } as Response)

    const error = await proposeCorrection({ window: dayWindow, correction: '纠正' }).catch(toApiError)

    expect(error).toEqual({
      kind: 'calendar',
      code: 'calendar_rules_mismatch',
      message: 'Browser and Analytics TZDB disagree.',
    })
  })
})
