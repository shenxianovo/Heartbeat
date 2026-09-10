import { afterEach, expect, it, vi } from 'vitest'
import { findCompatibleHub } from '../src/hub'
import { protocolFetch } from '../src/connection'

const binding = { profileId: 'a'.repeat(32), token: 'b'.repeat(64), port: 32001, buildId: 'build-a' }
function fixture(profileId = binding.profileId) {
  vi.stubEnv('MODE', 'development')
  vi.stubEnv('VITE_HEARTBEAT_BUILD_ID', 'build-a')
  const storage: Record<string, unknown> = {}
  vi.stubGlobal('chrome', {
    runtime: { getURL: (path: string) => `chrome-extension://dev/${path}` },
    storage: { local: { get: async () => storage, set: async (value: object) => Object.assign(storage, value) } },
  })
  const requests: { url: string; method?: string; redirect?: string }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input).startsWith('chrome-extension:')) return Response.json(binding)
    requests.push({ url: String(input), method: init?.method, redirect: init?.redirect })
    return Response.json({ binding: 'external-host', protocolMajors: [1], profileId })
  }))
  return { requests, storage }
}
afterEach(() => { vi.unstubAllEnvs(); vi.unstubAllGlobals() })

it('ignores production and cached ports and checks the specific Profile identity', async () => {
  const { requests } = fixture('wrong-profile')
  expect(await findCompatibleHub(24820, 24821)).toBe(null)
  expect(requests).toHaveLength(1)
  expect(requests[0].url).toBe(`http://127.0.0.1:32001/v1/collector-bindings/${binding.profileId}/${binding.token}`)
})

it('keeps every operation bound even when the target port is replaced after discovery', async () => {
  const { requests } = fixture()
  expect(await findCompatibleHub(24820, 24821)).toBe(32001)
  for (const suffix of ['/hello', '/session/initialize', '/session/ready', '/session/renew', '/session/facts', '/session/gap'])
    await protocolFetch(32001, suffix, { method: 'POST' })
  expect(requests.every(request => request.url.includes(`/collector-bindings/${binding.profileId}/${binding.token}`))).toBe(true)
  expect(requests.every(request => request.redirect === 'error')).toBe(true)
  await expect(protocolFetch(24820, '/hello', { method: 'POST' })).rejects.toThrow('different Desktop endpoint')
})

it('does not move retained extension data to a replacement Profile', async () => {
  const { requests, storage } = fixture()
  storage.developmentDesktopProfile = 'c'.repeat(32)
  storage.pendingSegments = { pending: 'retained' }
  expect(await findCompatibleHub(24820, 24820)).toBe(null)
  await expect(protocolFetch(32001, '/session/facts', { method: 'POST' })).rejects.toThrow('original Profile')
  expect(requests).toEqual([])
  expect(storage.pendingSegments).toEqual({ pending: 'retained' })
})

it('does not let old loaded code claim a newly staged Package before Reload', async () => {
  const { requests } = fixture()
  vi.stubEnv('VITE_HEARTBEAT_BUILD_ID', 'older-build')
  expect(await findCompatibleHub(24820, 24820)).toBe(null)
  await expect(protocolFetch(32001, '/hello', { method: 'POST' })).rejects.toThrow('Reload')
  expect(requests).toEqual([])
})
