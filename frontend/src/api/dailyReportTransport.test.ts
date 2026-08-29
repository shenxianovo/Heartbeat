// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchDailyReport,
  fetchPublicDailyReport,
  fetchPublicWeeklyReport,
  fetchWeeklyReport,
  toApiError,
} from './index'
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

const weekWindow: CalendarWindowEnvelope<'week'> = {
  version: 1,
  kind: 'week',
  localDate: '2026-03-08',
  timeZone: 'America/New_York',
  start: '2026-03-02T05:00:00Z',
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
    await fetchDailyReport({ deviceId: 7, window: dayWindow })

    expectRequest('/api/v1/reports/daily', '7', dayWindow)
  })

  it('sends identical window semantics to the public endpoint and omits all-device scope', async () => {
    await fetchPublicDailyReport('alice', { deviceId: 0, window: dayWindow })

    expectRequest('/api/v1/users/alice/reports/daily', null, dayWindow)
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

    const error = await fetchDailyReport({ window: dayWindow }).catch(toApiError)

    expect(error).toEqual({
      kind: 'calendar',
      code: 'calendar_rules_mismatch',
      message: 'Browser and Analytics TZDB disagree.',
    })
  })
})

describe('Weekly Report Local Calendar Window transport', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      text: async () => JSON.stringify({
        weekStart: '2026-03-02',
        weekEnd: '2026-03-08',
        apps: [],
      }),
    } as Response)
  })

  it('sends the complete 167-hour week envelope to the owner endpoint', async () => {
    await fetchWeeklyReport({ deviceId: 7, window: weekWindow })

    expectRequest('/api/v1/reports/weekly', '7', weekWindow)
  })

  it('sends identical window semantics to the public endpoint and omits all-device scope', async () => {
    await fetchPublicWeeklyReport('alice', { deviceId: 0, window: weekWindow })

    expectRequest('/api/v1/users/alice/reports/weekly', null, weekWindow)
  })
})

function expectRequest(
  path: string,
  deviceId: string | null,
  window: CalendarWindowEnvelope,
) {
  const [rawUrl] = fetchMock.mock.calls[0]
  const url = new URL(rawUrl, 'https://heartbeat.test')
  expect(url.pathname).toBe(path)
  expect(Object.fromEntries(url.searchParams)).toEqual({
    ...(deviceId == null ? {} : { deviceId }),
    Version: '1',
    Kind: window.kind,
    LocalDate: window.localDate,
    TimeZone: window.timeZone,
    Start: new Date(window.start).toISOString(),
    EndExclusive: new Date(window.endExclusive).toISOString(),
  })
  expect(url.searchParams.has('date')).toBe(false)
}
