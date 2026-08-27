// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchManagedSubjectStatuses } from '../api/index'
import LoginManagementView from './LoginManagementView.vue'

vi.mock('../api/index', () => ({
  fetchManagedSubjectStatuses: vi.fn(),
  submitManagedSubjectAuthorization: vi.fn(async () => undefined),
}))

describe('LoginManagementView', () => {
  beforeEach(() => vi.clearAllMocks())
  afterEach(() => vi.useRealTimers())

  it('loads every managed account and keeps no-status messaging in settings', async () => {
    vi.mocked(fetchManagedSubjectStatuses).mockResolvedValue([
      {
        subjectId: '0198d5df-5df3-70a1-937d-68a7d64623e2',
        subjectName: 'VRChat · Alice',
        subjectKind: 'Account',
        phase: 'Ready',
        authorization: null,
        currentActivity: null,
      },
      {
        subjectId: '0198d5df-5df3-70a1-937d-68a7d64623e5',
        subjectName: 'VRChat · Bob',
        subjectKind: 'Account',
        collectorInstanceId: '0198d5df-5df3-70a1-937d-68a7d64623e6',
        phase: 'WaitingForAuthorization',
        authorization: {
          interactionId: '0198d5df-5df3-70a1-937d-68a7d64623e7',
          kind: 'Credentials',
          title: '登录 VRChat',
          fields: [],
        },
        currentActivity: null,
      },
    ])

    const wrapper = mount(LoginManagementView, {
      global: {
        stubs: {
          Card: { template: '<section><slot /></section>' },
          Button: { template: '<button><slot /></button>' },
          RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
        },
      },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('VRChat · Alice')
    expect(wrapper.text()).toContain('暂无可展示的账号状态')
    expect(wrapper.text()).toContain('VRChat · Bob')
    expect(wrapper.text()).toContain('未登录')
    expect(wrapper.get('a[href="/settings"]').text()).toContain('返回设置')
    wrapper.unmount()
  })
})
