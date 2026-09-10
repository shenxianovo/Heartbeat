import { afterEach, expect, it, vi } from 'vitest'
import { reloadDevelopmentExtensionIfUpdated } from '../src/connection'

const profileId = 'a'.repeat(32)
function fixture() {
  vi.stubEnv('MODE', 'development')
  vi.stubEnv('VITE_HEARTBEAT_BUILD_ID', 'old-build')
  const storage: Record<string, unknown> = { developmentDesktopProfile: profileId, pendingSegments: { retained: true }, config: { enabled: true } }
  const events: string[] = []
  const reload = vi.fn(() => { events.push('reload') })
  vi.stubGlobal('chrome', {
    runtime: { getURL: (path: string) => `chrome-extension://dev/${path}`, reload },
    storage: { local: { get: async (key: string) => ({ [key]: storage[key] }), set: async (value: object) => Object.assign(storage, value) } }
  })
  const binding = { profileId, token: 'b'.repeat(64), port: 32001, buildId: 'new-build' }
  const fetchMock = vi.fn(async () => Response.json(binding))
  vi.stubGlobal('fetch', fetchMock)
  return { storage, events, reload, binding, fetchMock }
}
afterEach(() => { vi.unstubAllEnvs(); vi.unstubAllGlobals() })

it('persists activity before reloading a changed build and retains identity, config and pending data', async () => {
  const f = fixture()
  expect(await reloadDevelopmentExtensionIfUpdated(async () => { f.events.push('persist') })).toBe(true)
  expect(f.events).toEqual(['persist', 'reload'])
  expect(f.storage.developmentDesktopProfile).toBe(profileId)
  expect(f.storage.pendingSegments).toEqual({ retained: true })
  expect(f.storage.config).toEqual({ enabled: true })
})

it('never reloads production or a current build, and prevents repeated reloads of a broken build', async () => {
  const f = fixture(), persist = vi.fn()
  vi.stubEnv('MODE', 'production')
  expect(await reloadDevelopmentExtensionIfUpdated(persist)).toBe(false)
  expect(f.fetchMock).not.toHaveBeenCalled()
  vi.stubEnv('MODE', 'development')
  vi.stubEnv('VITE_HEARTBEAT_BUILD_ID', 'new-build')
  expect(await reloadDevelopmentExtensionIfUpdated(persist)).toBe(false)
  vi.stubEnv('VITE_HEARTBEAT_BUILD_ID', 'old-build')
  await reloadDevelopmentExtensionIfUpdated(persist)
  expect(await reloadDevelopmentExtensionIfUpdated(persist)).toBe(false)
  expect(f.reload).toHaveBeenCalledTimes(1)
})

it('refuses another or unpinned Profile, incomplete metadata, and failed persistence', async () => {
  const f = fixture(), persist = vi.fn()
  f.binding.profileId = 'c'.repeat(32)
  await expect(reloadDevelopmentExtensionIfUpdated(persist)).rejects.toThrow('Profile')
  f.binding.profileId = profileId
  delete f.storage.developmentDesktopProfile
  await expect(reloadDevelopmentExtensionIfUpdated(persist)).rejects.toThrow('Profile')
  f.storage.developmentDesktopProfile = profileId
  f.binding.buildId = ''
  await expect(reloadDevelopmentExtensionIfUpdated(persist)).rejects.toThrow('invalid')
  f.binding.buildId = 'new-build'
  await expect(reloadDevelopmentExtensionIfUpdated(async () => { throw new Error('storage unavailable') })).rejects.toThrow('storage unavailable')
  expect(f.reload).not.toHaveBeenCalled()
  expect(persist).not.toHaveBeenCalled()
  expect(f.storage.developmentReloadAttempt).toBeUndefined()
})
