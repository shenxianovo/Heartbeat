// @vitest-environment happy-dom

import { defineComponent } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useHeartbeat } from './useHeartbeat'
import { resolveCalendarContext } from '../calendar/localCalendarWindow'
import {
  fetchPublicApps,
  fetchPublicDailyReport,
  fetchPublicKeyFrequency,
  fetchPublicUsage,
  fetchPublicWeeklyReport,
} from '../api/index'

const calendarState = vi.hoisted(() => ({
  identity: 0,
  timeZone: 'America/New_York',
  isToday: false,
}))

vi.mock('../calendar/localCalendarWindow', async (importOriginal) => {
  const original = await importOriginal<typeof import('../calendar/localCalendarWindow')>()
  return {
    ...original,
    resolveCalendarContext: vi.fn(() => Object.freeze({
      day: Object.freeze({
        version: 1,
        kind: 'day',
        localDate: '2026-03-08',
        timeZone: calendarState.timeZone,
        start: calendarState.timeZone === 'America/New_York'
          ? '2026-03-08T05:00:00Z'
          : '2026-03-08T08:00:00Z',
        endExclusive: calendarState.timeZone === 'America/New_York'
          ? '2026-03-09T04:00:00Z'
          : '2026-03-09T07:00:00Z',
      }),
      week: Object.freeze({
        version: 1,
        kind: 'week',
        localDate: '2026-03-08',
        timeZone: calendarState.timeZone,
        start: calendarState.timeZone === 'America/New_York'
          ? '2026-03-02T05:00:00Z'
          : '2026-03-02T08:00:00Z',
        endExclusive: calendarState.timeZone === 'America/New_York'
          ? '2026-03-09T04:00:00Z'
          : '2026-03-09T07:00:00Z',
      }),
      isToday: calendarState.isToday,
      displayLabel: `2026-03-08 · ${calendarState.timeZone}`,
      correlationIdentity: `refresh-${++calendarState.identity}`,
    })),
  }
})

vi.mock('../stores/auth', () => ({
  authStore: {
    isAuthenticated: false,
    username: { value: null },
  },
}))

vi.mock('../api/index', () => ({
  fetchAdminAppCatalog: vi.fn(async () => ({ products: [] })),
  fetchMe: vi.fn(async () => ({ isAdmin: false })),
  fetchPublicApps: vi.fn(async () => []),
  fetchPublicDevices: vi.fn(async () => []),
  fetchPublicDeviceStatus: vi.fn(async () => ({})),
  fetchPublicDailyReport: vi.fn(async () => ({ date: '2026-03-08', apps: [] })),
  fetchPublicWeeklyReport: vi.fn(async () => ({ apps: [] })),
  fetchPublicUsage: vi.fn(async () => []),
  fetchPublicKeyFrequency: vi.fn(async () => []),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

describe('useHeartbeat activity view Calendar Context', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    calendarState.identity = 0
    calendarState.timeZone = 'America/New_York'
    calendarState.isToday = false
  })

  afterEach(() => vi.useRealTimers())

  it('passes one captured day window to Daily, Usage, and Key Frequency across device scope changes', async () => {
    let heartbeat!: ReturnType<typeof useHeartbeat>
    const wrapper = mount(defineComponent({
      setup() {
        heartbeat = useHeartbeat('alice')
        return () => null
      },
    }))
    await flushPromises()
    vi.clearAllMocks()

    await heartbeat.refresh()
    const day = heartbeat.calendarContext.value.day

    expect(fetchPublicDailyReport).toHaveBeenLastCalledWith('alice', {
      deviceId: 0,
      window: day,
    })
    expect(fetchPublicUsage).toHaveBeenLastCalledWith('alice', {
      deviceId: 0,
      start: day.start,
      end: day.endExclusive,
    })
    expect(fetchPublicKeyFrequency).toHaveBeenLastCalledWith('alice', {
      deviceId: 0,
      start: day.start,
      end: day.endExclusive,
    })
    expect(fetchPublicWeeklyReport).toHaveBeenCalled()

    heartbeat.selectedDevice.value = 42
    await flushPromises()
    const sameDay = heartbeat.calendarContext.value.day

    expect(sameDay).toEqual(day)
    expect(fetchPublicUsage).toHaveBeenLastCalledWith('alice', {
      deviceId: 42,
      start: day.start,
      end: day.endExclusive,
    })
    expect(fetchPublicKeyFrequency).toHaveBeenLastCalledWith('alice', {
      deviceId: 42,
      start: day.start,
      end: day.endExclusive,
    })

    wrapper.unmount()
  })

  it('resolves one immutable Calendar Context for the initial refresh and adopts travel timezone on the next refresh', async () => {
    let heartbeat!: ReturnType<typeof useHeartbeat>
    const wrapper = mount(defineComponent({
      setup() {
        heartbeat = useHeartbeat('alice')
        return () => null
      },
    }))
    await flushPromises()

    expect(resolveCalendarContext).toHaveBeenCalledTimes(1)
    const first = heartbeat.calendarContext.value
    expect(Object.isFrozen(first)).toBe(true)
    expect(Object.isFrozen(first.day)).toBe(true)
    expect(first.day.timeZone).toBe('America/New_York')
    expect(heartbeat.isToday.value).toBe(first.isToday)
    expect(heartbeat.timezoneLabel.value).toBe(first.displayLabel)
    expect(fetchPublicDailyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: first.day,
    })
    expect(fetchPublicWeeklyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: first.week,
    })
    expect(fetchPublicUsage).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      start: first.day.start,
      end: first.day.endExclusive,
    })
    expect(fetchPublicKeyFrequency).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      start: first.day.start,
      end: first.day.endExclusive,
    })

    calendarState.timeZone = 'America/Los_Angeles'
    expect(heartbeat.calendarContext.value).toBe(first)

    await heartbeat.refresh()
    const next = heartbeat.calendarContext.value

    expect(resolveCalendarContext).toHaveBeenCalledTimes(2)
    expect(next).not.toBe(first)
    expect(next.day.timeZone).toBe('America/Los_Angeles')
    expect(fetchPublicDailyReport).toHaveBeenLastCalledWith('alice', {
      deviceId: 0,
      window: next.day,
    })
    expect(fetchPublicWeeklyReport).toHaveBeenLastCalledWith('alice', {
      deviceId: 0,
      window: next.week,
    })

    wrapper.unmount()
  })

  it('does not let an older refresh generation overwrite newer ordinary response state', async () => {
    let heartbeat!: ReturnType<typeof useHeartbeat>
    const wrapper = mount(defineComponent({
      setup() {
        heartbeat = useHeartbeat('alice')
        return () => null
      },
    }))
    await flushPromises()

    type Apps = Awaited<ReturnType<typeof fetchPublicApps>>
    let resolveOld!: (value: Apps) => void
    let resolveNew!: (value: Apps) => void
    vi.mocked(fetchPublicApps)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve }))

    const oldRefresh = heartbeat.refresh()
    const newRefresh = heartbeat.refresh()

    resolveNew([{ id: 2, name: 'new-app' }] as Apps)
    await newRefresh
    resolveOld([{ id: 1, name: 'old-app' }] as Apps)
    await oldRefresh

    expect(heartbeat.appNameMap.value).toEqual(new Map([[2, 'new-app']]))

    wrapper.unmount()
  })

  it('does not create empty refresh generations while polling a historical Calendar Context', async () => {
    vi.useFakeTimers()
    let heartbeat!: ReturnType<typeof useHeartbeat>
    const wrapper = mount(defineComponent({
      setup() {
        heartbeat = useHeartbeat('alice')
        return () => null
      },
    }))
    await flushPromises()
    const historical = heartbeat.calendarContext.value

    await vi.advanceTimersByTimeAsync(30_000)

    expect(resolveCalendarContext).toHaveBeenCalledTimes(1)
    expect(heartbeat.calendarContext.value).toBe(historical)

    wrapper.unmount()
  })

  it('reuses the captured Calendar Context for the 30-second today poll', async () => {
    vi.useFakeTimers()
    calendarState.isToday = true
    let heartbeat!: ReturnType<typeof useHeartbeat>
    const wrapper = mount(defineComponent({
      setup() {
        heartbeat = useHeartbeat('alice')
        return () => null
      },
    }))
    await flushPromises()
    const captured = heartbeat.calendarContext.value
    vi.clearAllMocks()

    await vi.advanceTimersByTimeAsync(30_000)

    expect(resolveCalendarContext).not.toHaveBeenCalled()
    expect(heartbeat.calendarContext.value).toBe(captured)
    expect(fetchPublicDailyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: captured.day,
    })
    expect(fetchPublicWeeklyReport).toHaveBeenCalledWith('alice', {
      deviceId: 0,
      window: captured.week,
    })

    wrapper.unmount()
  })
})
