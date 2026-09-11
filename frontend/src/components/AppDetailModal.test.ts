// @vitest-environment happy-dom

import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CalendarContext, CalendarWindowEnvelope } from '../calendar/localCalendarWindow'
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

const observedMachine = { id: 'machine', kind: 'machine', scope: 'heartbeat.device', key: 'machine' }
const observedApp = { id: 'app', kind: 'app', scope: 'heartbeat.app', key: 'browser' }
const observationRelations = [{ kind: 'observed-on', members: [
  { role: 'device', object: observedMachine }, { role: 'app', object: observedApp },
] }]
const desktopEvidence = { foi: observedMachine, relations: observationRelations, aspect: 'desktop-activity' }
const pageEvidence = { foi: observedApp, relations: observationRelations, aspect: 'selected-page' }

function detailContext(
  day: CalendarWindowEnvelope<'day'>,
  correlationIdentity: string,
): Pick<CalendarContext, 'day' | 'correlationIdentity'> {
  return Object.freeze({ day, correlationIdentity })
}

const springContext = detailContext(springDay, 'refresh-1')
const nextContext = detailContext(nextDay, 'refresh-2')

describe('AppDetailModal Local Calendar Window adapter', () => {
  beforeEach(() => vi.clearAllMocks())

  it.each(['native', 'legacy-import'])('shows the browser page URL from %s Fact history in title details', async origin => {
    const url = 'https://example.com/page?q=original#section'
    const attributes = { url, site: 'example.com', domain: 'example.com', windowId: 1 }
    const identityKey = 'https://example.com/page'
    const title = 'Example page'
    vi.mocked(fetchPublicSegments).mockResolvedValueOnce([{
      ...pageEvidence, deviceId: 7, appId: 7, source: 'browser', identityKey, title,
      origin, payload: { identityKey, title, attributes },
      startTime: new Date('2026-03-08T06:00:00Z'),
      endTime: new Date('2026-03-08T07:00:00Z'),
    }] as never[])
    const wrapper = shallowMount(AppDetailModal, {
      props: {
        username: 'alice', deviceId: 7, calendarContext: springContext,
        app: { appId: 7, appName: 'Google Chrome', totalSeconds: 3600 },
        usageData: [{
          ...desktopEvidence, deviceId: 7, appId: 7, appKey: 'chrome', title: 'Example page - Google Chrome',
          startTime: new Date('2026-03-08T06:00:00Z'), endTime: new Date('2026-03-08T07:00:00Z'),
        }] as never[],
        devices: [], isProvisional: false,
      },
      global: { renderStubDefaultSlot: true, stubs: { Teleport: true, AppIcon: true } },
    })
    await flushPromises()

    const details = wrapper.findAll('section')[1]!
    expect(details.text()).toContain(title)
    expect(details.text()).toContain(url)
    wrapper.unmount()
  })

  it('queries Segments with the captured day endpoints and changes only device scope', async () => {
    const wrapper = shallowMount(AppDetailModal, {
      props: {
        username: 'alice',
        deviceId: 0,
        calendarContext: springContext,
        app: { appId: 7, appName: 'Code', totalSeconds: 120 },
        usageData: [],
        devices: [],
        isProvisional: false,
      },
      global: {
        renderStubDefaultSlot: true,
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
        calendarContext: springContext,
        app: { appId: 7, appName: 'Code', totalSeconds: 120 },
        usageData: [],
        devices: [],
        isProvisional: false,
      },
      global: {
        renderStubDefaultSlot: true,
        stubs: { Teleport: true, AppIcon: true },
      },
    })

    await wrapper.setProps({ calendarContext: nextContext })
    expect(fetchPublicSegments).toHaveBeenCalledTimes(2)
    expect(fetchPublicSegments).toHaveBeenNthCalledWith(1, 'alice', expect.objectContaining({
      start: springDay.start,
      end: springDay.endExclusive,
    }))
    expect(fetchPublicSegments).toHaveBeenNthCalledWith(2, 'alice', expect.objectContaining({
      start: nextDay.start,
      end: nextDay.endExclusive,
    }))

    resolveRequest([])
    await flushPromises()
  })

  it('clips title-detail duration to the captured day before aggregation', async () => {
    const wrapper = shallowMount(AppDetailModal, {
      props: {
        username: 'alice',
        deviceId: 0,
        calendarContext: springContext,
        app: { appId: 7, appName: 'Code', totalSeconds: 3600 },
        usageData: [{
          deviceId: 7,
          appId: 7,
          appKey: 'vscode',
          appName: 'Code',
          title: 'Work',
          startTime: new Date('2026-03-08T03:00:00Z'),
          endTime: new Date('2026-03-08T06:00:00Z'),
          durationSeconds: 10800,
        }] as never[],
        devices: [],
        isProvisional: false,
      },
      global: {
        renderStubDefaultSlot: true,
        stubs: { Teleport: true, AppIcon: true },
      },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('1h 0m')
    expect(wrapper.text()).not.toContain('3h 0m')
  })

  it('does not let a slow App Detail response from an older refresh generation overwrite the current detail', async () => {
    type Segments = Awaited<ReturnType<typeof fetchPublicSegments>>
    let resolveOld!: (value: Segments) => void
    let resolveNew!: (value: Segments) => void
    vi.mocked(fetchPublicSegments)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve }))

    const wrapper = shallowMount(AppDetailModal, {
      props: {
        username: 'alice',
        deviceId: 0,
        calendarContext: springContext,
        app: { appId: 7, appName: 'Code', totalSeconds: 3600 },
        usageData: [{
          ...desktopEvidence, deviceId: 7,
          appId: 7,
          appKey: 'browser',
          appName: 'Browser',
          title: 'Window',
          startTime: new Date('2026-03-08T06:00:00Z'),
          endTime: new Date('2026-03-08T07:00:00Z'),
          durationSeconds: 3600,
        }] as never[],
        devices: [],
        isProvisional: false,
      },
      global: {
        renderStubDefaultSlot: true,
        stubs: { Teleport: true, AppIcon: true },
      },
    })

    await wrapper.setProps({ calendarContext: detailContext(springDay, 'refresh-2') })
    expect(fetchPublicSegments).toHaveBeenCalledTimes(2)

    resolveNew([{
      ...pageEvidence, source: 'browser',
      identityKey: 'new-page',
      title: 'New page',
      startTime: new Date('2026-03-08T06:00:00Z'),
      endTime: new Date('2026-03-08T07:00:00Z'),
    }] as Segments)
    await flushPromises()
    resolveOld([{
      ...pageEvidence, source: 'browser',
      identityKey: 'old-page',
      title: 'Old page',
      startTime: new Date('2026-03-08T06:00:00Z'),
      endTime: new Date('2026-03-08T07:00:00Z'),
    }] as Segments)
    await flushPromises()

    expect(wrapper.text()).toContain('New page')
    expect(wrapper.text()).not.toContain('Old page')
  })
})

it('exposes an accessible dialog with a named close action', async () => {
  const { mount } = await import('@vue/test-utils')
  const wrapper = mount(AppDetailModal, {
    attachTo: document.body,
    props: { username: 'alice', deviceId: 0, calendarContext: springContext, app: { appId: 7, appName: 'Code', totalSeconds: 120 }, usageData: [], devices: [], isProvisional: false },
    global: { stubs: { AppIcon: true } },
  })
  await flushPromises()
  const dialog = document.querySelector('[role="dialog"]')
  expect(dialog).not.toBeNull()
  expect(document.getElementById(dialog!.getAttribute('aria-labelledby')!)?.textContent).toBe('Code')
  expect(dialog!.querySelector('button[aria-label="关闭应用详情"]')).not.toBeNull()
  wrapper.unmount()
})

it('keeps concurrent device tracks separate when using the shared replay template', async () => {
  vi.mocked(fetchPublicSegments).mockResolvedValueOnce([])
  const wrapper = shallowMount(AppDetailModal, {
    props: {
      username: 'alice', deviceId: 0, calendarContext: springContext,
      app: { appId: 7, appName: 'Code', totalSeconds: 7200 }, isProvisional: false,
      devices: [{ id: 1, name: 'Laptop' }, { id: 2, name: 'Desktop' }] as never[],
      usageData: [1, 2].map(deviceId => ({ deviceId, foi: { id: `machine-${deviceId}`, kind: 'machine', scope: 'heartbeat.device', key: `machine-${deviceId}`, name: deviceId === 1 ? 'Laptop' : 'Desktop' }, appId: 7, appName: 'Code', title: `Work ${deviceId}`,
        startTime: new Date('2026-03-08T06:00:00Z'), endTime: new Date('2026-03-08T07:00:00Z') })) as never[],
    },
    global: { renderStubDefaultSlot: true, stubs: { AppIcon: true } },
  })
  await flushPromises()
  const replay = wrapper.findAll('section')[0]
  expect(replay.text()).toContain('Laptop')
  expect(replay.text()).toContain('Desktop')
  expect(replay.findAll('[title]').length).toBe(2)
  wrapper.unmount()
})

it('keeps account objects separate without inventing device identities', async () => {
  vi.mocked(fetchPublicSegments).mockResolvedValueOnce(['Alice account', 'Second account'].map((name, i) => ({
    foi: { id: `account-${i}`, kind: 'account', scope: 'vrchat', key: `account-${i}`, name },
    source: 'vrchat.account', identityKey: `world-${i}`, title: `World ${i}`,
    payload: { identityKey: `world-${i}`, title: `World ${i}`, attributes: {} },
    startTime: new Date('2026-03-08T06:00:00Z'), endTime: new Date('2026-03-08T07:00:00Z'),
  })) as never[])
  const wrapper = shallowMount(AppDetailModal, {
    props: {
      username: 'alice', deviceId: 0, calendarContext: springContext,
      app: { appId: 7, appName: 'VRChat', totalSeconds: 0 }, isProvisional: false,
      devices: [], usageData: [],
    },
    global: { renderStubDefaultSlot: true, stubs: { AppIcon: true } },
  })
  await flushPromises()
  const replay = wrapper.findAll('section')[0]!
  expect(replay.text()).toContain('Alice account')
  expect(replay.text()).toContain('Second account')
  expect(replay.text()).not.toContain('设备 0')
  expect(replay.findAll('[title]').length).toBe(2)
  wrapper.unmount()
})


it('replays streamless observations from two collectors without hiding their unknown source', async () => {
  vi.mocked(fetchPublicSegments).mockResolvedValueOnce(['collector-a', 'collector-b'].map((collectorId, i) => ({
    id: `native-${i}`, streamId: null, factId: null, collectorId, source: null, aspect: 'selected-page',
    foi: { id: 'browser', kind: 'app', scope: 'heartbeat.app', key: 'browser', name: 'Browser' },
    title: `Independent page ${i}`, payload: { attributes: { windowId: 1, url: `https://example.com/${i}` } },
    startTime: new Date('2026-03-08T06:00:00Z'), endTime: new Date('2026-03-08T07:00:00Z'),
  })) as never[])
  const wrapper = shallowMount(AppDetailModal, {
    props: { username: 'alice', deviceId: 0, calendarContext: springContext,
      app: { appId: 7, appName: 'Browser', totalSeconds: 0 }, isProvisional: false, devices: [], usageData: [] },
    global: { renderStubDefaultSlot: true, stubs: { AppIcon: true } },
  })
  await flushPromises()
  const replay = wrapper.findAll('section')[0]!
  expect(replay.findAll('[title]')).toHaveLength(2)
  expect(replay.text()).toContain('未知来源')
  expect(replay.findAll('[title]').map(bar => bar.attributes('title'))).toEqual([
    expect.stringContaining('Independent page 0'), expect.stringContaining('Independent page 1'),
  ])
  wrapper.unmount()
})


it.each(['account-location', 'unlinked-page', 'other-device'])('does not upgrade foreground labels from %s observations', async kind => {
  const machine = { id: 'machine-a', kind: 'machine', scope: 'heartbeat.device', key: 'machine-a' }
  const app = { id: 'browser', kind: 'app', scope: 'heartbeat.app', key: 'browser' }
  const relations = (device = machine) => [{ kind: 'observed-on', members: [
    { role: 'device', object: device }, { role: 'app', object: app },
  ] }]
  vi.mocked(fetchPublicSegments).mockResolvedValueOnce([{
    id: 'candidate', collectorId: 'collector', source: null,
    aspect: kind === 'account-location' ? kind : 'selected-page', foi: app,
    relations: kind === 'unlinked-page' ? [] : relations(kind === 'other-device' ? { ...machine, id: 'machine-b' } : machine),
    title: 'Unrelated observation', identityKey: 'unrelated',
    payload: { attributes: { url: 'https://unrelated.example/' } },
    startTime: new Date('2026-03-08T06:00:00Z'), endTime: new Date('2026-03-08T07:00:00Z'),
  }] as never[])
  const wrapper = shallowMount(AppDetailModal, {
    props: { username: 'alice', deviceId: 0, calendarContext: springContext,
      app: { appId: 7, appName: 'Browser', totalSeconds: 3600 }, isProvisional: false, devices: [],
      usageData: [{ appId: 7, aspect: 'desktop-activity', source: null, foi: machine, relations: relations(),
        title: 'Actual foreground', startTime: new Date('2026-03-08T06:00:00Z'), endTime: new Date('2026-03-08T07:00:00Z') }] as never[] },
    global: { renderStubDefaultSlot: true, stubs: { AppIcon: true } },
  })
  await flushPromises()
  const titles = wrapper.findAll('section')[1]!
  expect(titles.text()).toContain('Actual foreground')
  expect(titles.text()).not.toContain('Unrelated observation')
  expect(wrapper.findAll('section')[0]!.findAll('[title]').some(bar => bar.attributes('title')!.includes('Unrelated observation'))).toBe(true)
  wrapper.unmount()
})
