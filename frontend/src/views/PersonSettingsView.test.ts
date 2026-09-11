// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import PersonSettingsView from './PersonSettingsView.vue'
import { fetchPersonSettings, establishPerson, savePersonAssociation, removePersonAssociation, fetchPersonFacts } from '../api/person'

vi.mock('../api/person', () => ({
  fetchPersonSettings: vi.fn(), establishPerson: vi.fn(), savePersonAssociation: vi.fn(),
  removePersonAssociation: vi.fn(), fetchPersonFacts: vi.fn(),
}))

beforeEach(() => vi.clearAllMocks())

it('maintains a confirmed association and refreshes the person history after corrections and removal', async () => {
  const settings = { person: { id: 'person', reference: '11111111-1111-4111-8111-111111111111' },
    objects: [{ kind: 'machine', id: 'machine', scope: 'heartbeat.device', key: 'Mac', name: 'Offline Mac' }], associations: [] as import('../api/person').PersonAssociation[] }
  vi.mocked(fetchPersonSettings).mockImplementation(async () => structuredClone(settings))
  vi.mocked(fetchPersonFacts).mockResolvedValue({ totalCount: 1, sources: [{ source: 'system', count: 1 }], items: [{
    fact: { id: 'f', source: 'system', foi: { id: 'machine', kind: 'machine', scope: 'heartbeat.device', key: 'Mac', name: 'Fixture Object' }, start: '2026-09-01T01:00:00Z', end: '2026-09-01T01:10:00Z', payload: { title: 'Historical work' } },
    effectiveIntervals: [{ start: '2026-09-01T01:02:00Z', end: '2026-09-01T01:05:00Z' }], effectiveSeconds: 180,
  }] })
  vi.mocked(savePersonAssociation).mockImplementation(async (id, value) => {
    settings.associations = [{ id: id ?? '5', ...value }]
  })
  vi.mocked(removePersonAssociation).mockImplementation(async () => { settings.associations = [] })
  const wrapper = mount(PersonSettingsView, { global: { stubs: { RouterLink: true } } })
  await flushPromises()
  await wrapper.get('[aria-label="关联设备或账号"]').setValue('machine')
  await wrapper.get('[aria-label="适用起点"]').setValue('2026-09-01T09:02')
  await wrapper.get('[aria-label="适用终点"]').setValue('2026-09-01T09:05')
  await wrapper.get('form[aria-label="维护本人关联"]').trigger('submit')
  await flushPromises()
  expect(wrapper.text()).toContain('Offline Mac')
  expect(wrapper.text()).toContain('Historical work')
  expect(wrapper.text()).toContain('原始区间')
  expect(wrapper.text()).toContain('有效覆盖')
  expect(wrapper.text()).toContain('180')
  await wrapper.get('[aria-label="纠正关联 5"]').trigger('click')
  await wrapper.get('[aria-label="适用终点"]').setValue('2026-09-01T09:06')
  await wrapper.get('form[aria-label="维护本人关联"]').trigger('submit')
  await flushPromises()
  expect(savePersonAssociation).toHaveBeenLastCalledWith('5', expect.objectContaining({ objectId: 'machine' }))
  await wrapper.get('[aria-label="移除关联 5"]').trigger('click')
  await flushPromises()
  expect(wrapper.find('[aria-label="纠正关联 5"]').exists()).toBe(false)
  expect(fetchPersonFacts).toHaveBeenCalledTimes(4)
})

it('requires explicit confirmation for all history, preserves microsecond bounds, and surfaces failures', async () => {
  const settings = { person: { id: 'person', reference: 'self' }, objects: [{ kind: 'account', id: 'account', scope: 'vrchat', key: 'historical', name: 'vrchat · historical' }],
    associations: [{ id: '5', objectId: 'account', start: '2026-09-01T01:02:00.000123Z', end: '2026-09-01T01:02:00.000456Z' }] }
  vi.mocked(fetchPersonSettings).mockResolvedValue(settings)
  vi.mocked(fetchPersonFacts).mockResolvedValue({ items: [], sources: [], totalCount: 0 })
  const wrapper = mount(PersonSettingsView, { global: { stubs: { RouterLink: true } } })
  await flushPromises()
  await wrapper.get('[aria-label="关联设备或账号"]').setValue('account')
  await wrapper.get('form[aria-label="维护本人关联"]').trigger('submit')
  await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('确认全部历史')
  expect(savePersonAssociation).not.toHaveBeenCalled()
  await wrapper.get('[aria-label="纠正关联 5"]').trigger('click')
  await wrapper.get('form[aria-label="维护本人关联"]').trigger('submit')
  await flushPromises()
  expect(savePersonAssociation).toHaveBeenLastCalledWith('5', { objectId: 'account', start: settings.associations[0]!.start, end: settings.associations[0]!.end })
  vi.mocked(savePersonAssociation).mockRejectedValue(new Error('保存失败'))
  await wrapper.get('[aria-label="关联设备或账号"]').setValue('account')
  await wrapper.get('input[type="checkbox"]').setValue(true)
  await wrapper.get('form[aria-label="维护本人关联"]').trigger('submit')
  await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toBe('保存失败')
})

it('establishes self only after an explicit action', async () => {
  vi.mocked(fetchPersonSettings).mockResolvedValue({ person: null, objects: [], associations: [] })
  vi.mocked(establishPerson).mockResolvedValue(undefined)
  vi.mocked(fetchPersonFacts).mockResolvedValue({ items: [], sources: [], totalCount: 0 })
  const wrapper = mount(PersonSettingsView, { global: { stubs: { RouterLink: true } } })
  await flushPromises()
  expect(establishPerson).not.toHaveBeenCalled()
  await wrapper.get('[aria-label="建立本人资料"]').trigger('click')
  await flushPromises()
  expect(establishPerson).toHaveBeenCalledOnce()
})

it('does not expose an input key sequence in event details', async () => {
  vi.mocked(fetchPersonSettings).mockResolvedValue({ person: null, objects: [], associations: [] })
  vi.mocked(fetchPersonFacts).mockResolvedValue({ totalCount: 1, sources: [{ source: 'system', count: 1 }], items: [{
    fact: { id: 'event', source: 'system', foi: { id: 'machine', kind: 'machine', scope: 'heartbeat.device', key: 'Mac', name: 'Fixture Object' }, occurredAt: '2026-09-01T01:00:00Z', payload: { eventType: 'keyDown', code: 65, codeSet: 'windows-vk-v1' } },
    effectiveIntervals: [], effectiveSeconds: null,
  }] })
  const wrapper = mount(PersonSettingsView, { global: { stubs: { RouterLink: true } } })
  await flushPromises()
  expect(wrapper.text()).toContain('发生时刻')
  expect(wrapper.text()).not.toContain('keyDown')
  expect(wrapper.text()).not.toContain('windows-vk-v1')
})
