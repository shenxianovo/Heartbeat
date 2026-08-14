import { describe, expect, it, vi } from 'vitest'
import { loadAdminProvisionalAppIds, type AdminOverlayDeps } from './adminOverlay'

function deps(isAdmin: boolean): AdminOverlayDeps {
  return {
    isAuthenticated: true,
    currentUsername: 'alice',
    fetchMe: vi.fn(async () => ({ username: 'alice', isPublic: false, isAdmin })),
    fetchInventory: vi.fn(async () => ({
      products: [
        { id: 1, isProvisional: false },
        { id: 2, isProvisional: true },
      ],
    })),
  }
}

describe('admin provisional overlay', () => {
  it('does not request privileged inventory for an ordinary owner', async () => {
    const d = deps(false)

    const ids = await loadAdminProvisionalAppIds('alice', d)

    expect([...ids]).toEqual([])
    expect(d.fetchInventory).not.toHaveBeenCalled()
  })

  it('returns provisional product ids only for an administrator viewing their own dashboard', async () => {
    const d = deps(true)

    const ids = await loadAdminProvisionalAppIds('alice', d)

    expect([...ids]).toEqual([2])
  })

  it('does not expose the overlay on a public or another user dashboard', async () => {
    const d = deps(true)

    const ids = await loadAdminProvisionalAppIds('bob', d)

    expect([...ids]).toEqual([])
    expect(d.fetchMe).not.toHaveBeenCalled()
  })
})
