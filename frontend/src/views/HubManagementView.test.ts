// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import HubManagementView from './HubManagementView.vue'

const api = vi.hoisted(() => ({
  fetchManagedCollectors: vi.fn(),
  installManagedCollector: vi.fn(async () => undefined),
  uninstallManagedCollector: vi.fn(async () => undefined),
  retryManagedCollector: vi.fn(async () => undefined),
  submitCollectorAuthorization: vi.fn(async () => undefined),
}))

vi.mock('../api/index', () => api)

describe('HubManagementView', () => {
  beforeEach(() => vi.clearAllMocks())
  afterEach(() => vi.useRealTimers())

  it('lists generic catalog entries without Collector-specific production knowledge', async () => {
    api.fetchManagedCollectors.mockResolvedValue([{
      packageId: 'heartbeat.collector.reference',
      displayName: 'Reference Collector',
      summary: 'A generic Collector',
      latestVersion: '1.0.0',
      isInstalled: false,
      phase: 'NotInstalled',
    }])
    const wrapper = mount(HubManagementView, {
      global: { stubs: {
        Card: { template: '<section><slot /></section>' },
        Button: { template: '<button><slot /></button>' },
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      } },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('Hub 管理')
    expect(wrapper.text()).toContain('Reference Collector')
    expect(wrapper.text()).toContain('安装')
    expect(wrapper.get('a[href="/settings"]').text()).toContain('返回设置')
    wrapper.unmount()
  })

  it('keeps refreshing after an accepted command until the Collector state changes', async () => {
    vi.useFakeTimers()
    const firstAuthorization = {
      packageId: 'heartbeat.collector.reference',
      displayName: 'Reference Collector',
      summary: 'Generic fixture',
      isInstalled: true,
      installedVersion: '1.0.0',
      collectorInstanceId: '0198d5df-5df3-70a1-937d-68a7d64623e3',
      phase: 'WaitingForAuthorization',
      authorization: {
        interactionId: '0198d5df-5df3-70a1-937d-68a7d64623e4',
        kind: 'Credentials' as const,
        title: 'Sign in',
        fields: [{ name: 'token', label: 'Token', isSecret: true }],
      },
    }
    const secondAuthorization = {
      ...firstAuthorization,
      authorization: {
        interactionId: '0198d5df-5df3-70a1-937d-68a7d64623e5',
        kind: 'VerificationCode' as const,
        title: 'Enter verification code',
        fields: [{ name: 'code', label: 'Code', isSecret: false }],
      },
    }
    api.fetchManagedCollectors
      .mockResolvedValueOnce([firstAuthorization])
      .mockResolvedValueOnce([firstAuthorization])
      .mockResolvedValueOnce([secondAuthorization])

    const wrapper = mount(HubManagementView, {
      global: { stubs: {
        Card: { template: '<section><slot /></section>' },
        Button: { props: ['type', 'disabled'], template: '<button :type="type" :disabled="disabled"><slot /></button>' },
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      } },
    })
    await flushPromises()

    await wrapper.get('input').setValue('secret')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(api.fetchManagedCollectors).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('已提交，等待采集器响应…')

    await vi.advanceTimersByTimeAsync(1_000)
    await flushPromises()

    expect(api.fetchManagedCollectors).toHaveBeenCalledTimes(3)
    expect(wrapper.text()).toContain('Enter verification code')
    expect(wrapper.text()).not.toContain('已提交，等待采集器响应…')

    await vi.advanceTimersByTimeAsync(10_000)
    expect(api.fetchManagedCollectors).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })
})
