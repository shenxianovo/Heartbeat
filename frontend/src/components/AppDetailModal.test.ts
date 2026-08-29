// @vitest-environment happy-dom

import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CalendarWindowEnvelope } from '../calendar/localCalendarWindow'
import AppDetailModal from './AppDetailModal.vue'
import { fetchPublicSegments } from '../api/index'

vi.mock('../api/index', () => ({
  fetchPublicSegments: vi.fn(async () => []),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

vi.mock('../stores/auth', () => ({
  authStore: {
    isAuthenticated: false,
    username: { value: '' },
  },
}))

const springDay: CalendarWindowEnvelope<'day'> = Object.freeze({
  version: 1,
  kind: 'day',
  localDate: '2026-03-08',
  timeZone: 'America/New_York',
  start: '2026-03-08T05:00:00Z',
  endExclusive: '2026-03-09T04:00:00Z',
})

const nextDay: CalendarWindowEnvelope<'day'> = Object.freeze({
  ...springDay,
  localDate: '2026-03-09',
  start: '2026-03-09T04:00:00Z',
  endExclusive: '2026-03-10T04:00:00Z',
})

describe('AppDetailModal Local Calendar Window adapter', () => {
  beforeEach(() => vi.clearAllMocks())

  it('queries Segments with the captured day endpoints and changes only device scope', async () => {
    const wrapper = shallowMount(AppDetailModal, {
      props: {
        username: 'alice',
        deviceId: 0,
        dayWindow: springDay,
        app: { appId: 7, appName: 'Code', totalSeconds: 120 },
        usageData: [],
        devices: [],
        isProvisional: false,
      },
      global: {
        stubs: { Teleport: true, AppIcon: true },
      },
    })
    await flushPromises()

    expect(fetchPublicSegments).toHaveBeenLastCalledWith('alice', {
      deviceId: 0,
      appId: 7,
      start: springDay.start,
      end: springDay.endExclusive,
    })

    await wrapper.setProps({ deviceId: 42 })
    await flushPromises()

    expect(fetchPublicSegments).toHaveBeenLastCalledWith('alice', {
      deviceId: 42,
      appId: 7,
      start: springDay.start,
      end: springDay.endExclusive,
    })
  })

  it('does not reinterpret an in-flight detail request from a newer selected date', async () => {
    let resolveRequest!: (value: []) => void
    vi.mocked(fetchPublicSegments).mockImplementationOnce(
      () => new Promise(resolve => { resolveRequest = resolve }),
    )
    const wrapper = shallowMount(AppDetailModal, {
      props: {
        username: 'alice',
        deviceId: 0,
        dayWindow: springDay,
        app: { appId: 7, appName: 'Code', totalSeconds: 120 },
        usageData: [],
        devices: [],
        isProvisional: false,
      },
      global: {
        stubs: { Teleport: true, AppIcon: true },
      },
    })

    await wrapper.setProps({ dayWindow: nextDay })
    expect(fetchPublicSegments).toHaveBeenCalledTimes(1)
    expect(fetchPublicSegments).toHaveBeenCalledWith('alice', expect.objectContaining({
      start: springDay.start,
      end: springDay.endExclusive,
    }))

    resolveRequest([])
    await flushPromises()
  })
})
