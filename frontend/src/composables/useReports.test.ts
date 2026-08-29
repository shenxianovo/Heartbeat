import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CalendarContext } from '../calendar/localCalendarWindow'
import { useReports } from './useReports'
import {
  fetchPublicDailyReport,
  fetchPublicUsage,
} from '../api/index'

vi.mock('../api/index', () => ({
  fetchPublicDailyReport: vi.fn(async () => ({ date: '2026-03-08', apps: [] })),
  fetchPublicWeeklyReport: vi.fn(async () => ({ apps: [] })),
  fetchPublicUsage: vi.fn(async () => []),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

const context: CalendarContext = Object.freeze({
  day: Object.freeze({
    version: 1,
    kind: 'day',
    localDate: '2026-03-08',
    timeZone: 'America/New_York',
    start: '2026-03-08T05:00:00Z',
    endExclusive: '2026-03-09T04:00:00Z',
  }),
  isToday: false,
  displayLabel: '2026-03-08 · America/New_York (UTC-05:00 → UTC-04:00)',
  correlationIdentity: 'refresh-1',
})

describe('useReports Local Calendar Window', () => {
  beforeEach(() => vi.clearAllMocks())

  it('reuses one captured context for Daily Report and the generic usage adapter', async () => {
    const reports = useReports('alice', ref(0), ref('2026-03-08'), ref(context))

    await Promise.all([reports.loadDaily(), reports.loadUsage()])

    expect(fetchPublicDailyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: context.day,
    })
    expect(fetchPublicUsage).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      start: context.day.start,
      end: context.day.endExclusive,
    })
  })

  it('does not let an older refresh overwrite the current Daily Report', async () => {
    type DailyReport = Awaited<ReturnType<typeof fetchPublicDailyReport>>
    let resolveOld!: (value: DailyReport) => void
    let resolveNew!: (value: DailyReport) => void
    vi.mocked(fetchPublicDailyReport)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve }))

    const current = ref(context)
    const reports = useReports('alice', ref(0), ref('2026-03-08'), current)
    const oldRequest = reports.loadDaily()
    current.value = Object.freeze({ ...context, correlationIdentity: 'refresh-2' })
    const newRequest = reports.loadDaily()

    resolveNew({ date: '2026-03-08', apps: [{ appId: 2, appName: 'new', durationSeconds: 2 }] } as DailyReport)
    await newRequest
    resolveOld({ date: '2026-03-08', apps: [{ appId: 1, appName: 'old', durationSeconds: 1 }] } as DailyReport)
    await oldRequest

    expect(reports.appSummaries.value.map(app => app.appName)).toEqual(['new'])
  })
})
