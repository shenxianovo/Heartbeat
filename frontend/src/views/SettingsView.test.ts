// @vitest-environment happy-dom

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchMe } from '../api/index'
import SettingsView from './SettingsView.vue'

vi.mock('../api/index', () => ({
  fetchMe: vi.fn(),
  updateMySettings: vi.fn(),
}))

vi.mock('../stores/auth', () => ({
  authStore: { logout: vi.fn() },
}))

describe('SettingsView administrator navigation', () => {
  beforeEach(() => vi.clearAllMocks())

  it('shows App Catalog only to deployment administrators', async () => {
    vi.mocked(fetchMe).mockResolvedValue({ username: 'alice', isPublic: false, isAdmin: true })

    const wrapper = mount(SettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('App Catalog')
  })

  it('does not render the management entry for an ordinary user', async () => {
    vi.mocked(fetchMe).mockResolvedValue({ username: 'alice', isPublic: false, isAdmin: false })

    const wrapper = mount(SettingsView, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
    })
    await flushPromises()

    expect(wrapper.text()).not.toContain('App Catalog')
  })
})
