// @vitest-environment happy-dom

import { defineComponent } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useHeartbeat } from './useHeartbeat'
import {
  fetchPublicDailyReport,
  fetchPublicKeyFrequency,
  fetchPublicUsage,
  fetchPublicWeeklyReport,
} from '../api/index'

const calendarState = vi.hoisted(() => ({ identity: 0 }))

vi.mock('../calendar/localCalendarWindow', async (importOriginal) => {
  const original = await importOriginal<typeof import('../calendar/localCalendarWindow')>()
  return {
    ...original,
    resolveCalendarContext: vi.fn(() => Object.freeze({
      day: Object.freeze({
        version: 1,
        kind: 'day',
        localDate: '2026-03-08',
        timeZone: 'America/New_York',
        start: '2026-03-08T05:00:00Z',
        endExclusive: '2026-03-09T04:00:00Z',
      }),
      week: Object.freeze({
        version: 1,
        kind: 'week',
        localDate: '2026-03-08',
        timeZone: 'America/New_York',
        start: '2026-03-02T05:00:00Z',
        endExclusive: '2026-03-09T04:00:00Z',
      }),
      isToday: false,
      displayLabel: '2026-03-08 · America/New_York',
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
})
