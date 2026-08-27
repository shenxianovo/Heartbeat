// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ManagedSubjectLoginCard from './ManagedSubjectLoginCard.vue'

const { submitManagedSubjectAuthorization } = vi.hoisted(() => ({
  submitManagedSubjectAuthorization: vi.fn(async () => undefined),
}))

vi.mock('../api/index', () => ({ submitManagedSubjectAuthorization }))

const stubs = {
  Card: { template: '<section><slot /></section>' },
  Button: {
    props: ['type', 'disabled'],
    template: '<button :type="type" :disabled="disabled"><slot /></button>',
  },
}

describe('ManagedSubjectLoginCard', () => {
  beforeEach(() => vi.clearAllMocks())

  it('submits a credentials challenge from login management', async () => {
    const wrapper = mount(ManagedSubjectLoginCard, {
      props: {
        subject: {
          subjectId: '0198d5df-5df3-70a1-937d-68a7d64623e2',
          subjectName: 'VRChat · Alice',
          subjectKind: 'Account',
          collectorInstanceId: '0198d5df-5df3-70a1-937d-68a7d64623e3',
          phase: 'WaitingForAuthorization',
          authorization: {
            interactionId: '0198d5df-5df3-70a1-937d-68a7d64623e4',
            kind: 'Credentials',
            title: '登录 VRChat',
            message: '会话只保存在 Hub。',
            fields: [
              { name: 'username', label: '用户名或邮箱', isSecret: false },
              { name: 'password', label: '密码', isSecret: true },
            ],
          },
          currentActivity: null,
        },
      },
      global: { stubs },
    })

    expect(wrapper.text()).toContain('未登录')
    const inputs = wrapper.findAll('input')
    await inputs[0].setValue('alice')
    await inputs[1].setValue('secret')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(submitManagedSubjectAuthorization).toHaveBeenCalledWith(
      '0198d5df-5df3-70a1-937d-68a7d64623e3',
      '0198d5df-5df3-70a1-937d-68a7d64623e4',
      { username: 'alice', password: 'secret' },
    )
    expect(wrapper.emitted('submitted')).toHaveLength(1)
    expect(wrapper.text()).toContain('已提交，等待采集器响应')
  })

  it('shows an authorized account with no activity as logged in without status', () => {
    const wrapper = mount(ManagedSubjectLoginCard, {
      props: {
        subject: {
          subjectId: '0198d5df-5df3-70a1-937d-68a7d64623e2',
          subjectName: 'VRChat · Alice',
          subjectKind: 'Account',
          phase: 'Ready',
          authorization: null,
          currentActivity: null,
        },
      },
      global: { stubs },
    })

    expect(wrapper.text()).toContain('已登录')
    expect(wrapper.text()).toContain('暂无可展示的账号状态')
    expect(wrapper.find('form').exists()).toBe(false)
  })
})
