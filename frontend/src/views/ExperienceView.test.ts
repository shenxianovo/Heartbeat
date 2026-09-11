// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchExperiencePage } from '../api'
import ExperienceView from './ExperienceView.vue'
import ActivitySwimlanes from '../experience/ActivitySwimlanes.vue'
import type { ExperienceSegment } from '../experience/factViews'

vi.mock('vue-router', () => ({ useRoute: () => ({ params: { username: 'alice' } }) }))
vi.mock('../api', () => ({ fetchExperiencePage: vi.fn() }))

const machine = { id: 'machine', kind: 'machine', scope: 'heartbeat.device', key: 'Mac', name: 'Mac' }
const app = { id: 'app', kind: 'app', scope: 'heartbeat.app', key: 'browser', name: 'Browser' }
const makeFact = (id: string, start: string, source = 'system'): ExperienceSegment => ({
  id, factId: id, streamId: 'stream', revision: 1, deviceId: 1, foi: source === 'browser' ? app : machine,
  relations: [{ id: `r-${id}`, kind: 'observed-on', validFrom: start, validTo: start,
    evidence: { factId: id }, members: [{ role: 'device', object: machine }, { role: 'app', object: app }] }],
  source, aspect: source === 'system' ? 'desktop-activity' : source === 'browser' ? 'selected-page' : source,
  appId: 1, appIdentityId: 1, appName: 'Browser', appKey: 'browser', startTime: start,
  endTime: new Date(Date.parse(start) + 1000).toISOString(), payload: { title: `record-${id}` },
})
function setup() {
  return mount(ExperienceView, { global: { stubs: {
    RouterLink: { template: '<a><slot /></a>' },
    DatePicker: { props: ['modelValue'], emits: ['update:modelValue'], template: '<input class="date" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />' },
    AppIcon: true,
    FactLane: { props: ['facts'], emits: ['select', 'range'], template: '<div class="lane" />' },
    ActivityOverview: { props: ['range'], emits: ['range'], template: '<div class="overview" />' },
  } } })
}
describe('Experience page', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-09-09T00:15:00'))
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
  })
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })
  it('follows all pages, keeps browser observations accessible and focuses a real record', async () => {
    vi.mocked(fetchExperiencePage).mockImplementation(async (_user, window, after) => ({
      items: after ? [makeFact('browser', window.start, 'browser')]
        : [makeFact('system', window.start)], nextCursor: after ? null : 'next',
    }))
    const wrapper = setup(); await flushPromises()
    expect(fetchExperiencePage).toHaveBeenCalledTimes(2)
    expect(wrapper.findAll('.lane')).toHaveLength(2)
    await wrapper.findAll('.fact-main').find(button => button.text().includes('record-system'))!.trigger('click')
    expect(wrapper.find('.selection').text()).toContain('相关 Browser 观察 1')
    await wrapper.get('.selection .fact-actions > button').trigger('click')
    expect(wrapper.find('.selection').exists()).toBe(true)
    const range = wrapper.getComponent(ActivitySwimlanes).props('range')
    expect(range.end - range.start).toBeLessThan(86_400_000)
    expect(wrapper.findAll('.toolbar button')).toHaveLength(0)
    expect(wrapper.find('.interaction-help').exists()).toBe(false)
    expect(wrapper.find('.lane').exists()).toBe(true)
    expect(wrapper.find('.records-section').exists()).toBe(true)
    expect(wrapper.find('.overview').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('5分钟')
    wrapper.unmount()
  })
  it('shows native identity, opaque results and precise page relations without old delivery metadata', async () => {
    const opaque = { title: 'not-a-known-title', nested: [null, false, { extra: '保留字段' }] }
    vi.mocked(fetchExperiencePage).mockImplementation(async (_user, window) => {
      const native = (id: string, source: string, override = {}) => ({
        ...makeFact(id, window.start, source), streamId: null, factId: null, source: null,
        collectorId: `collector-${id}`, ...override,
      }) as unknown as ExperienceSegment
      return { items: [native('desktop', 'system'), native('page-a', 'browser'), native('page-b', 'browser'),
        native('opaque', 'system', { aspect: 'custom', payload: opaque, relations: [] })], nextCursor: null }
    })
    const wrapper = setup(); await flushPromises()
    const records = wrapper.get('.records-section')
    const desktop = records.findAll('.fact-card').find(card => card.find('strong').text() === 'record-desktop')!
    expect(desktop.text()).toContain('desktop / 1')
    expect(desktop.text()).toContain('collector-desktop')
    expect(desktop.text()).toContain('desktop-activity')
    expect(records.text()).toContain('未知来源')
    expect(records.findAll('strong').some(title => title.text() === 'not-a-known-title')).toBe(false)
    expect(records.findAll('pre').map(pre => JSON.parse(pre.text()))).toContainEqual(opaque)
    await desktop.get('.fact-main').trigger('click')
    expect(wrapper.get('.selection').text()).toContain('相关 Browser 观察 2')
    await wrapper.get('select[aria-label="筛选对象"]').setValue('app')
    expect(wrapper.find('.selection').exists()).toBe(false)
    expect(wrapper.get('.record-count').text()).toBe('2')
    expect(wrapper.get('.records-section').text()).toContain('record-page-a')
    expect(wrapper.get('.records-section').text()).toContain('record-page-b')
    expect(wrapper.get('.records-section').text()).not.toContain('record-desktop')
    wrapper.unmount()
  })
  it('initializes history from the earliest fact after paging and preserves an explored window on refresh', async () => {
    vi.mocked(fetchExperiencePage).mockImplementation(async (_user, window, after) => ({
      items: [makeFact(after ? 'early' : 'late', new Date(Date.parse(window.start) + (after ? 3 : 15) * 3600_000).toISOString())],
      nextCursor: after ? null : 'more',
    }))
    const wrapper = setup(); await flushPromises()
    await wrapper.get('input.date').setValue('2026-01-02'); await flushPromises()
    const lanes = wrapper.getComponent(ActivitySwimlanes)
    const bounds = lanes.props('bounds')
    expect(lanes.props('range')).toEqual({ start: bounds.start + 2 * 3600_000, end: bounds.start + 4 * 3600_000 })
    const explored = { start: bounds.start + 14 * 3600_000, end: bounds.start + 16 * 3600_000 }
    lanes.vm.$emit('range', explored); await flushPromises()
    await wrapper.get('.date-tools button').trigger('click'); await flushPromises()
    expect(lanes.props('range')).toEqual(explored)
    wrapper.unmount()
  })
  it('discards a superseded date request even when its transport resolves after cancellation', async () => {
    let finishOld: (value: { items: ExperienceSegment[]; nextCursor: null }) => void = () => {}
    vi.mocked(fetchExperiencePage).mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
      .mockImplementation(async (_user, window) => ({ items: [makeFact('new', window.start)], nextCursor: null }))
    const wrapper = setup(); await flushPromises()
    const oldSignal = vi.mocked(fetchExperiencePage).mock.calls[0][3]
    await wrapper.get('input.date').setValue('2026-01-02'); await flushPromises()
    expect(oldSignal.aborted).toBe(true)
    finishOld({ items: [makeFact('old', '2026-01-01T00:00:00Z')], nextCursor: null }); await flushPromises()
    expect(wrapper.text()).toContain('record-new')
    expect(wrapper.text()).not.toContain('record-old')
    wrapper.unmount()
  })
  it('labels partial results as incomplete when a later page fails', async () => {
    vi.mocked(fetchExperiencePage).mockImplementationOnce(async (_user, window) => ({ items: [makeFact('one', window.start)], nextCursor: 'more' }))
      .mockRejectedValueOnce(new Error('网络中断'))
    const wrapper = setup(); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('结果不完整')
    expect(wrapper.text()).toContain('record-one')
    wrapper.unmount()
  })
})
