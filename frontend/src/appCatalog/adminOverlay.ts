export interface AdminOverlayDeps {
  isAuthenticated: boolean
  currentUsername: string | null
  fetchMe: () => Promise<{ isAdmin: boolean }>
  fetchInventory: () => Promise<{
    products?: { id?: number; isProvisional?: boolean }[]
  }>
}

export async function loadAdminProvisionalAppIds(
  viewedUsername: string,
  deps: AdminOverlayDeps,
): Promise<Set<number>> {
  if (!deps.isAuthenticated || deps.currentUsername !== viewedUsername) return new Set()

  const me = await deps.fetchMe()
  if (!me.isAdmin) return new Set()

  const inventory = await deps.fetchInventory()
  return new Set(
    (inventory.products ?? [])
      .filter(product => product.isProvisional && product.id !== undefined)
      .map(product => product.id!),
  )
}
