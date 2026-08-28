// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import StatusCards from './StatusCards.vue'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: 'tok-private-owner' },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

vi.mock('../composables/useHeartbeat', () => ({
  formatDuration: vi.fn((seconds: number) => `${seconds}s`),
}))

function mountCards() {
  return mount(StatusCards, {
    props: {
      username: 'alice',
      isToday: true,
      isAlive: true,
      lastSeenStr: '',
      lastSeenTitle: '',
      appSummaries: [{ appId: 42, appName: 'Visual Studio Code', totalSeconds: 60 }],
      totalSeconds: 60,
      awaySeconds: 0,
      onlineSeconds: 60,
      perDeviceSeconds: [],
      hasConcurrentUse: false,
      isAllDevices: false,
      includeAway: false,
    },
    global: {
      stubs: {
        Card: { template: '<section><slot /></section>' },
      },
    },
  })
}

describe('StatusCards app icon', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
    vi.stubGlobal('URL', {
      createObjectURL: vi.fn(() => 'blob:authenticated-icon'),
      revokeObjectURL: vi.fn(),
    })
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      blob: async () => new Blob(['png'], { type: 'image/png' }),
    } as Response)
  })

  it('loads a private owner icon through the authenticated API path', async () => {
    const wrapper = mountCards()
    await flushPromises()

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/v1/users/alice/apps/42/icon')
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok-private-owner')
    expect(wrapper.get('img').attributes('src')).toBe('blob:authenticated-icon')
  })
})
