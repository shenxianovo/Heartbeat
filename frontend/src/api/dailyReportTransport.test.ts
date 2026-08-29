// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchDailyReport, fetchPublicDailyReport, toApiError } from './index'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: 'tok-owner' },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

const fetchMock = vi.fn()
const window: CalendarWindowEnvelope = {
  version: 1,
  kind: 'day',
  localDate: '2026-03-08',
  timeZone: 'America/New_York',
  start: '2026-03-08T05:00:00Z',
  endExclusive: '2026-03-09T04:00:00Z',
}

describe('Daily Report Local Calendar Window transport', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({ date: '2026-03-08', apps: [] }),
    } as Response)
  })

  it('sends the complete immutable envelope to the owner endpoint', async () => {
    await fetchDailyReport({ deviceId: 7, window })

    expectRequest('/api/v1/reports/daily', '7')
  })

  it('sends identical window semantics to the public endpoint and omits all-device scope', async () => {
    await fetchPublicDailyReport('alice', { deviceId: 0, window })

    expectRequest('/api/v1/users/alice/reports/daily', null)
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

    const error = await fetchDailyReport({ window }).catch(toApiError)

    expect(error).toEqual({
      kind: 'calendar',
      code: 'calendar_rules_mismatch',
      message: 'Browser and Analytics TZDB disagree.',
    })
  })
})

function expectRequest(path: string, deviceId: string | null) {
  const [rawUrl] = fetchMock.mock.calls[0]
  const url = new URL(rawUrl, 'https://heartbeat.test')
  expect(url.pathname).toBe(path)
  expect(Object.fromEntries(url.searchParams)).toEqual({
    ...(deviceId == null ? {} : { deviceId }),
    Version: '1',
    Kind: 'day',
    LocalDate: '2026-03-08',
    TimeZone: 'America/New_York',
    Start: '2026-03-08T05:00:00.000Z',
    EndExclusive: '2026-03-09T04:00:00.000Z',
  })
  expect(url.searchParams.has('date')).toBe(false)
}
