// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchManagedCollectors } from '../api/index'
import HubManagementView from './HubManagementView.vue'

vi.mock('../api/index', () => ({
  fetchManagedCollectors: vi.fn(),
  installManagedCollector: vi.fn(async () => undefined),
  uninstallManagedCollector: vi.fn(async () => undefined),
  retryManagedCollector: vi.fn(async () => undefined),
  submitCollectorAuthorization: vi.fn(async () => undefined),
}))

describe('HubManagementView', () => {
  beforeEach(() => vi.clearAllMocks())

  it('lists generic catalog entries without Collector-specific production knowledge', async () => {
    vi.mocked(fetchManagedCollectors).mockResolvedValue([{
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
})
