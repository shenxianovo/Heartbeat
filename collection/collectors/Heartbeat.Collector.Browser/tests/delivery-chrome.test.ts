import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import { ChromeBrowserDeliveryStore } from '../src/delivery-chrome'
import { defaultBrowserDeliverySession } from '../src/delivery'

class MemoryStorageArea {
  values: Record<string, unknown>

  constructor(initial: Record<string, unknown> = {}) {
    this.values = structuredClone(initial)
  }

  async get(keys?: string | string[] | Record<string, unknown> | null) {
    if (keys === undefined || keys === null) return structuredClone(this.values)
    const names = typeof keys === 'string'
      ? [keys]
      : Array.isArray(keys) ? keys : Object.keys(keys)
    return Object.fromEntries(names
      .filter((key) => this.values[key] !== undefined)
      .map((key) => [key, structuredClone(this.values[key])]))
  }

  async set(items: Record<string, unknown>) {
    Object.assign(this.values, structuredClone(items))
  }

  async remove(keys: string | string[]) {
    for (const key of typeof keys === 'string' ? [keys] : keys) delete this.values[key]
  }
}

function installChrome(
  local: Record<string, unknown> = {},
  session: Record<string, unknown> = {},
) {
  const localArea = new MemoryStorageArea(local)
  const sessionArea = new MemoryStorageArea(session)
  vi.stubGlobal('chrome', { storage: { local: localArea, session: sessionArea } })
  return { localArea, sessionArea }
}

const legacySnapshot = {
  id: '0198d5eb-fc31-7d7b-8bf0-000000000001',
  source: 'browser',
  identityKey: 'https://example.com/page',
  appName: 'msedge',
  title: 'Example',
  startTime: '2026-08-25T08:00:00.000Z',
  endTime: '2026-08-25T08:01:00.000Z',
  attributes: {
    url: 'https://example.com/page',
    domain: 'example.com',
    site: 'example.com',
    windowId: 7,
  },
}

afterEach(() => vi.unstubAllGlobals())

describe('ChromeBrowserDeliveryStore adapter contract', () => {
  it('recovers the existing Chrome layout without leaking legacy fields', async () => {
    installChrome({
      pendingSegments: { [legacySnapshot.id]: legacySnapshot },
      browserCollectorPendingGap: {
        start: legacySnapshot.startTime,
        end: legacySnapshot.endTime,
        reason: 'buffer_overflow',
        estimatedFactsLost: 2,
      },
      browserCollectorDeadLetters: [legacySnapshot],
    }, {
      browserCollectorDesiredEnabled: false,
      browserCollectorFlushPeriodMs: 60_000,
      backoff: { fails: 2, nextAttemptAt: 123_000 },
    })
    const store = new ChromeBrowserDeliveryStore('edge')

    const durable = await store.loadDurable()
    const recovered = durable.queue[legacySnapshot.id]

    expect(recovered).not.toHaveProperty('appName')
    expect(recovered).toMatchObject({ appHint: 'edge', isFinal: false })
    expect(durable.pendingGaps).toHaveLength(1)
    expect(durable.policy).toEqual({ enabled: false, flushPeriodMilliseconds: 60_000 })
    await expect(store.loadSession()).resolves.toMatchObject({
      backoff: { fails: 2, nextAttemptAt: 123_000 },
    })
  })

  it('persists collection policy in local storage across a browser restart', async () => {
    const { sessionArea } = installChrome()
    const store = new ChromeBrowserDeliveryStore('edge')
    const durable = await store.loadDurable()
    durable.policy = { enabled: false, flushPeriodMilliseconds: 90_000 }
    await store.saveDurable(durable)

    sessionArea.values = {}

    await expect(new ChromeBrowserDeliveryStore('edge').loadDurable()).resolves.toMatchObject({
      policy: { enabled: false, flushPeriodMilliseconds: 90_000 },
    })
  })

  it('round-trips session attempts and removes obsolete optional fields', async () => {
    installChrome()
    const store = new ChromeBrowserDeliveryStore('edge')
    const state = {
      ...defaultBrowserDeliverySession(),
      hubPort: 24_821,
      publishAttempt: {
        activationId: '0198d5e8-30cb-7d54-bab1-250087147e4c',
        messageId: '0198d5eb-fc31-7d7b-8bf0-000000000010',
        snapshots: [{ ...legacySnapshot, appHint: 'edge', isFinal: false } as SegmentSnapshot],
      },
    }
    await store.saveSession(state)
    await expect(store.loadSession()).resolves.toEqual(state)

    await store.saveSession(defaultBrowserDeliverySession())
    await expect(store.loadSession()).resolves.toEqual(defaultBrowserDeliverySession())
  })
})
