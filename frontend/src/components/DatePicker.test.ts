// @vitest-environment happy-dom

import { CalendarDate } from '@internationalized/date'
import { mount } from '@vue/test-utils'
import { defineComponent, nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DatePicker from './DatePicker.vue'

const PopoverStub = defineComponent({
  name: 'Popover',
  props: { open: Boolean },
  emits: ['update:open'],
  template: '<div data-test="popover" :data-open="String(open)"><slot /></div>',
})

const CalendarStub = defineComponent({
  name: 'Calendar',
  props: ['modelValue', 'maxValue'],
  emits: ['update:modelValue'],
  template: '<div data-test="calendar" />',
})

const ButtonStub = defineComponent({
  name: 'Button',
  emits: ['click'],
  template: '<button @click="$emit(\'click\')"><slot /></button>',
})

function mountPicker(contextLabel?: string) {
  return mount(DatePicker, {
    props: { modelValue: '2026-08-01', contextLabel },
    global: {
      stubs: {
        Popover: PopoverStub,
        PopoverTrigger: { template: '<button><slot /></button>' },
        PopoverContent: { template: '<div><slot /></div>' },
        Calendar: CalendarStub,
        Button: ButtonStub,
        CalendarIcon: true,
      },
    },
  })
}

describe('DatePicker', () => {
  afterEach(() => vi.useRealTimers())

  it('renders the calendar window context as the single trigger label', () => {
    const wrapper = mountPicker('2026-08-01 · Asia/Shanghai (UTC+08:00)')

    expect(wrapper.get('button').text()).toBe('2026-08-01 · Asia/Shanghai (UTC+08:00)')
    expect(wrapper.get('button').text().match(/2026-08-01/g)).toHaveLength(1)
  })

  it('caps the calendar at browser-local today', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date(2026, 7, 16, 12, 0, 0))

    const wrapper = mountPicker()

    expect(wrapper.getComponent(CalendarStub).props('maxValue').toString()).toBe('2026-08-16')
  })

  it('selects a date and closes the popover immediately', async () => {
    const wrapper = mountPicker()
    const popover = wrapper.getComponent(PopoverStub)
    popover.vm.$emit('update:open', true)
    await nextTick()

    wrapper.getComponent(CalendarStub).vm.$emit('update:modelValue', new CalendarDate(2026, 8, 10))
    await nextTick()

    expect(wrapper.emitted('update:modelValue')).toEqual([['2026-08-10']])
    expect(wrapper.get('[data-test="popover"]').attributes('data-open')).toBe('false')
  })

  it('selects browser-local today from the footer', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date(2026, 7, 16, 12, 0, 0))
    const wrapper = mountPicker()

    const todayButton = wrapper.findAll('button').find(button => button.text() === '今天')
    expect(todayButton).toBeDefined()
    await todayButton!.trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([['2026-08-16']])
  })
})
