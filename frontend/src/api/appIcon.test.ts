// @vitest-environment happy-dom

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fetchAppIcon } from './index'

vi.mock('../stores/auth', () => ({
  authStore: {
    token: { value: 'tok-private-owner' },
    tryRefresh: vi.fn(),
    clearAuth: vi.fn(),
  },
}))

const fetchMock = vi.fn()

describe('fetchAppIcon', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.stubGlobal('fetch', fetchMock)
  })

  it('adds the owner bearer token before reading an icon blob', async () => {
    const icon = new Blob(['png'], { type: 'image/png' })
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      blob: async () => icon,
    } as Response)

    await expect(fetchAppIcon('alice', 42)).resolves.toBe(icon)

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/v1/users/alice/apps/42/icon')
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer tok-private-owner')
  })

  it('treats a missing icon as an empty visual instead of a dashboard error', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 404 } as Response)

    await expect(fetchAppIcon('alice', 404)).resolves.toBeNull()
  })
})
