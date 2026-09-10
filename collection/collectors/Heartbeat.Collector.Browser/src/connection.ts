// Development builds cannot fall back to production, even after clearing extension storage.
export interface DevelopmentBinding {
  profileId: string
  token: string
  port: number
  buildId: string
}

const PIN_KEY = 'developmentDesktopProfile'
const RELOAD_KEY = 'developmentReloadAttempt'
export const isDevelopment = () => import.meta.env.MODE === 'development'

async function readDevelopmentBinding(): Promise<DevelopmentBinding> {
  const response = await fetch(chrome.runtime.getURL('desktop-binding.json'), { cache: 'no-store' })
  if (!response.ok) throw new Error('Development Desktop binding is unavailable')
  const binding = await response.json() as DevelopmentBinding
  if (!/^[a-f0-9]{32}$/.test(binding.profileId) || !/^[a-f0-9]{64}$/.test(binding.token) ||
      !Number.isInteger(binding.port) || binding.port < 1024 || binding.port > 65535 ||
      typeof binding.buildId !== 'string' || !binding.buildId)
    throw new Error('Development Desktop binding is invalid')
  return binding
}

export async function loadDevelopmentBinding(): Promise<DevelopmentBinding> {
  const binding = await readDevelopmentBinding()
  if (!import.meta.env.VITE_HEARTBEAT_BUILD_ID || binding.buildId !== import.meta.env.VITE_HEARTBEAT_BUILD_ID)
    throw new Error('Development extension updated; Reload before reconnecting')
  const stored = await chrome.storage.local.get(PIN_KEY)
  if (stored[PIN_KEY] !== undefined && stored[PIN_KEY] !== binding.profileId)
    throw new Error('Development Desktop Profile changed; retained data belongs to the original Profile')
  if (stored[PIN_KEY] === undefined) await chrome.storage.local.set({ [PIN_KEY]: binding.profileId })
  return binding
}

/** Called inside the background event queue so current observations reach durable storage before Reload. */
export async function reloadDevelopmentExtensionIfUpdated(persistActivity: () => Promise<void>): Promise<boolean> {
  if (!isDevelopment()) return false
  const binding = await readDevelopmentBinding()
  const currentBuild = import.meta.env.VITE_HEARTBEAT_BUILD_ID
  if (!currentBuild) throw new Error('Development build identity is unavailable')
  if (binding.buildId === currentBuild) return false
  const stored = await chrome.storage.local.get(PIN_KEY)
  if (stored[PIN_KEY] !== binding.profileId)
    throw new Error('Automatic Reload requires the original pinned Desktop Profile')
  const attempted = await chrome.storage.local.get(RELOAD_KEY)
  if (attempted[RELOAD_KEY] === binding.buildId) return false
  await persistActivity()
  // A broken build must not put the browser into an endless Reload loop.
  await chrome.storage.local.set({ [RELOAD_KEY]: binding.buildId })
  chrome.runtime.reload()
  return true
}

export function bindingRoute(binding: DevelopmentBinding): string {
  return `/v1/collector-bindings/${binding.profileId}/${binding.token}`
}

/** Every request uses the bound route, including renew/retry after a port has been reused. */
export async function protocolFetch(port: number, suffix: string, init: RequestInit): Promise<Response> {
  let route = '/v1/collector-protocol/external-host'
  if (isDevelopment()) {
    const binding = await loadDevelopmentBinding()
    if (port !== binding.port) throw new Error('Cached session belongs to a different Desktop endpoint')
    route = bindingRoute(binding)
  }
  return fetch(`http://127.0.0.1:${port}${route}${suffix}`, { ...init, redirect: 'error',
    signal: init.signal ?? AbortSignal.timeout(10_000) })
}
