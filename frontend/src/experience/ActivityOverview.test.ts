// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ActivityOverview from './ActivityOverview.vue'
import { dragOverview } from './overviewRange'

describe('Activity overview selection', () => {
  const bounds = { start: 0, end: 100_000 }
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null)
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ left: 0, width: 1000 } as DOMRect)
    Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', { configurable: true, value: vi.fn() })
  })
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })
  it('brushes a new range from a full-day overview and restores it on double click', async () => {
    const wrapper = mount(ActivityOverview, { props: { facts: [], range: bounds, bounds, timeZone: 'UTC' } })
    await wrapper.get('.overview-move').trigger('pointerdown', { button: 0, pointerId: 1, clientX: 200 })
    await wrapper.get('.overview-track').trigger('pointermove', { pointerId: 1, clientX: 600 })
    await wrapper.get('.overview-track').trigger('pointerup', { pointerId: 1, clientX: 600 })
    expect(wrapper.emitted('range')?.slice(-1)[0]).toEqual([{ start: 20_000, end: 60_000 }])
    await wrapper.setProps({ range: { start: 20_000, end: 60_000 } })
    await wrapper.get('.overview-track').trigger('dblclick')
    expect(wrapper.emitted('range')?.slice(-1)[0]).toEqual([bounds])
    wrapper.unmount()
  })
  it('resizes an edge independently and allows keyboard movement without buttons', async () => {
    const wrapper = mount(ActivityOverview, { props: { facts: [], range: { start: 20_000, end: 60_000 }, bounds, timeZone: 'UTC' } })
    await wrapper.get('[aria-label="范围起点"]').trigger('pointerdown', { button: 0, pointerId: 1, clientX: 200 })
    await wrapper.get('.overview-track').trigger('pointermove', { pointerId: 1, clientX: 400 })
    await wrapper.get('.overview-track').trigger('pointerup', { pointerId: 1, clientX: 400 })
    expect(wrapper.emitted('range')?.slice(-1)[0]).toEqual([{ start: 40_000, end: 60_000 }])
    await wrapper.get('[aria-label="范围终点"]').trigger('keydown', { key: 'End' })
    expect(wrapper.emitted('range')?.slice(-1)[0]).toEqual([{ start: 20_000, end: 100_000 }])
    await wrapper.get('[aria-label="移动时间范围"]').trigger('keydown', { key: 'ArrowRight' })
    expect(wrapper.emitted('range')?.slice(-1)[0]).toEqual([{ start: 21_000, end: 61_000 }])
    wrapper.unmount()
  })
  it('keeps edges from crossing and preserves span when panning against a DST day boundary', () => {
    const day = { start: 0, end: 23 * 3600_000 }, range = { start: 2000, end: 6000 }
    expect(dragOverview('start', range, day, 2000, 9000)).toEqual({ start: 5000, end: 6000 })
    expect(dragOverview('end', range, day, 6000, 0)).toEqual({ start: 2000, end: 3000 })
    expect(dragOverview('move', range, day, 0, day.end)).toEqual({ start: day.end - 4000, end: day.end })
    expect(dragOverview('select', day, day, 6000, 2000)).toEqual(range)
  })
})
