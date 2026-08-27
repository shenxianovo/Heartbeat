// @vitest-environment happy-dom

import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import CurrentAppPanel from './CurrentAppPanel.vue'

vi.mock('../api/index', () => ({
  getIconUrl: vi.fn(() => '/icon.svg'),
}))

function mountPanel(overrides: Record<string, unknown> = {}) {
  return mount(CurrentAppPanel, {
    props: {
      username: 'alice',
      isToday: true,
      isAlive: false,
      currentApp: null,
      currentAppId: null,
      currentAppKey: null,
      presences: [],
      isAllDevices: true,
      ...overrides,
    },
    global: {
      stubs: {
        Card: { template: '<section><slot /></section>' },
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      },
    },
  })
}

describe('CurrentAppPanel', () => {
  it('hides the whole card when no device is online', () => {
    const wrapper = mountPanel({
      presences: [{
        deviceId: 1,
        deviceName: 'Old laptop',
        isOnline: false,
        currentApp: null,
        currentAppId: null,
        currentAppKey: null,
        currentAppIdentityKey: null,
        lastSeen: new Date(2026, 7, 12),
      }],
    })

    expect(wrapper.text()).not.toContain('当前使用')
    expect(wrapper.text()).not.toContain('Old laptop')
  })

  it('renders only online devices in the multi-device view', () => {
    const wrapper = mountPanel({
      isAlive: true,
      presences: [
        {
          deviceId: 1,
          deviceName: 'Online laptop',
          isOnline: true,
          currentApp: 'Visual Studio Code',
          currentAppId: 1,
          currentAppKey: 'vscode',
          currentAppIdentityKey: 'mac:com.microsoft.vscode',
          lastSeen: new Date(),
        },
        {
          deviceId: 2,
          deviceName: 'Online desktop',
          isOnline: true,
          currentApp: 'Terminal',
          currentAppId: 2,
          currentAppKey: 'terminal',
          currentAppIdentityKey: 'win:terminal',
          lastSeen: new Date(),
        },
        {
          deviceId: 3,
          deviceName: 'Offline desktop',
          isOnline: false,
          currentApp: null,
          currentAppId: null,
          currentAppKey: null,
          currentAppIdentityKey: null,
          lastSeen: new Date(2026, 7, 12),
        },
      ],
    })

    expect(wrapper.text()).toContain('Online laptop')
    expect(wrapper.text()).toContain('Online desktop')
    expect(wrapper.text()).not.toContain('Offline desktop')
  })

  it('shows the device name when exactly one device is online', () => {
    const wrapper = mountPanel({
      isAlive: true,
      currentApp: 'Visual Studio Code',
      currentAppId: 1,
      currentAppKey: 'vscode',
      presences: [{
        deviceId: 1,
        deviceName: 'MacBook Pro',
        isOnline: true,
        currentApp: 'Visual Studio Code',
        currentAppId: 1,
        currentAppKey: 'vscode',
        currentAppIdentityKey: 'mac:com.microsoft.vscode',
        lastSeen: new Date(),
      }],
    })

    expect(wrapper.text()).toContain('Visual Studio Code')
    expect(wrapper.text()).toContain('MacBook Pro')
  })

  it('shows only a settings entry for an account that needs login', () => {
    const wrapper = mountPanel({
      managedSubjects: [{
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
      }],
    })

    expect(wrapper.text()).toContain('当前使用')
    expect(wrapper.text()).toContain('未登录')
    expect(wrapper.get('a[href="/settings/logins"]').text()).toBe('去设置')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('does not show a login action for an authorized account', () => {
    const wrapper = mountPanel({
      managedSubjects: [{
        subjectId: '0198d5df-5df3-70a1-937d-68a7d64623e2',
        subjectName: 'VRChat · Alice',
        subjectKind: 'Account',
        phase: 'Ready',
        authorization: null,
        currentActivity: {
          title: 'Mock World',
          identityKey: 'instance:mock',
          startTime: '2026-08-27T00:00:00Z',
          endTime: '2026-08-27T01:00:00Z',
        },
      }],
    })

    expect(wrapper.text()).toContain('Mock World')
    expect(wrapper.findAll('button')).toHaveLength(0)
  })

  it('does not put an authorized account without activity in current usage', () => {
    const wrapper = mountPanel({
      managedSubjects: [{
        subjectId: '0198d5df-5df3-70a1-937d-68a7d64623e2',
        subjectName: 'VRChat · Alice',
        subjectKind: 'Account',
        phase: 'Ready',
        authorization: null,
        currentActivity: null,
      }],
    })

    expect(wrapper.text()).not.toContain('当前使用')
    expect(wrapper.text()).not.toContain('已登录')
  })
})
