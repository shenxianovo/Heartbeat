// @vitest-environment happy-dom

import { defineComponent, ref } from 'vue'
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchPublicDeviceStatus } from '../api/index'
import { useDeviceStatus } from './useDeviceStatus'

vi.mock('../api/index', () => ({
  fetchPublicDeviceStatus: vi.fn(),
  toApiError: vi.fn(() => ({ kind: 'parse' })),
}))

describe('useDeviceStatus refresh generation isolation', () => {
  beforeEach(() => vi.clearAllMocks())

  it('does not let a slower status response from an older generation replace the current presence', async () => {
    type Status = Awaited<ReturnType<typeof fetchPublicDeviceStatus>>
    let resolveOld!: (value: Status) => void
    let resolveNew!: (value: Status) => void
    vi.mocked(fetchPublicDeviceStatus)
      .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve }))

    let generation = 1
    let status!: ReturnType<typeof useDeviceStatus>
    const wrapper = mount(defineComponent({
      setup() {
        status = useDeviceStatus(
          'alice',
          ref([{ id: 7, name: 'Laptop' }] as never[]),
          ref(7),
          ref(true),
        )
        return () => null
      },
    }))

    const oldLoad = status.load(() => generation === 1)
    generation = 2
    const newLoad = status.load(() => generation === 2)
    resolveNew({ isOnline: true, currentApp: 'New app' } as Status)
    await newLoad
    resolveOld({ isOnline: true, currentApp: 'Old app' } as Status)
    await oldLoad

    expect(status.currentApp.value).toBe('New app')
    wrapper.unmount()
  })
})
