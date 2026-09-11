import { afterEach, expect, it, vi } from 'vitest'
import { ChromeBrowserDeliveryStore } from '../src/delivery-chrome'
import { ChromeBrowserDeliveryStore as PreviousStore } from './fixtures/browser-storage/delivery-chrome-e6564fc'
import { applyEvent, browserPayloadOf, emptyState, flush, type FoldDeps } from '../src/fold'
import { createSegmentSdk } from '../src/sdk/segments'
import { activityKeyOf } from '../src/normalize'
import { createBrowserDelivery } from '../src/delivery'
import generation1 from './fixtures/browser-storage/generation-1.json'
import generation2 from './fixtures/browser-storage/generation-2.json'
import generation3 from './fixtures/browser-storage/generation-3.json'
import generation4 from './fixtures/browser-storage/generation-4.json'

class StorageArea {
  constructor(public values: Record<string, unknown> = {}) {}
  async get(keys?: string | string[]) {
    return structuredClone(keys === undefined ? this.values : Object.fromEntries(
      (typeof keys === 'string' ? [keys] : keys).filter(key => key in this.values).map(key => [key, this.values[key]])))
  }
  async set(values: Record<string, unknown>) { Object.assign(this.values, structuredClone(values)) }
  async remove(keys: string | string[]) {
    for (const key of typeof keys === 'string' ? [keys] : keys) delete this.values[key]
  }
}

const id = '0198d5eb-fc31-7d7b-8bf0-000000000001'
const legacy = {
  id, source: 'browser', identityKey: 'https://example.com/page', appName: 'msedge', title: 'Before update',
  startTime: '2026-08-25T08:00:00.000Z', endTime: '2026-08-25T08:01:00.000Z',
  attributes: { url: 'https://example.com/page', domain: 'example.com', site: 'example.com', windowId: 7,
    unknownNestedResult: { preserved: ['exact', 7] } }, unknownResult: { preserved: true },
}
const legacyFold = { open: { 7: { id, startTime: Date.parse(legacy.startTime),
  identityKey: legacy.identityKey, url: legacy.attributes.url, title: legacy.title, windowId: 7 } } }
function fixture(local: Record<string, unknown> = {}, session: Record<string, unknown> = {}) {
  const localArea = new StorageArea(structuredClone(local)), sessionArea = new StorageArea(structuredClone(session))
  vi.stubGlobal('chrome', { storage: { local: localArea, session: sessionArea } })
  return { localArea, sessionArea }
}
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

it.each([generation1, generation2, generation3, generation4])(
  'restores historical storage generation $generation ($definitionCommit) and keeps identities through failed migration and restart', async entry => {
    const f = fixture(entry.local, entry.session), before = structuredClone(f.localArea.values)
    vi.spyOn(f.localArea, 'set').mockRejectedValueOnce(new Error('migration write unavailable'))
    await expect(new ChromeBrowserDeliveryStore().loadDurable()).rejects.toThrow('migration write unavailable')
    expect(f.localArea.values).toEqual(before)
    expect(f.sessionArea.values).toEqual(entry.session)
    const state = await new ChromeBrowserDeliveryStore().loadDurable()
    expect(state.queue[id]).toMatchObject({ id, endTime: '2026-08-25T08:01:00.000Z', isFinal: false,
      attributes: { unknownReading: { values: [1, 'two', null] } } })
    expect(state.queue[id].kind).toBeUndefined()
    expect(state.queue[id].revision).toBeUndefined()
    expect(state.foldState!.open[7]).toMatchObject({ id, revision: 1787644860000 })
    expect(state.deadLetters).toHaveLength(1)
    expect(state.pendingGaps).toHaveLength(1)
    expect(state.policy).toEqual({ enabled: false, flushPeriodMilliseconds: 60_000 })
    await new ChromeBrowserDeliveryStore().loadSession()
    expect(await new ChromeBrowserDeliveryStore().loadDurable()).toEqual(state)
    expect(f.localArea.values.browserCollectorExternalHostIdentity).toEqual(before.browserCollectorExternalHostIdentity)
  },
)

it('migrates actual old fold, queue and dead-letter keys without dropping result fields or ACK high water', async () => {
  const f = fixture({ pendingSegments: { [id]: legacy }, browserCollectorDeadLetters: [legacy, { ...legacy, title: 'Conflict evidence' }] },
    { foldState: legacyFold })
  const state = await new ChromeBrowserDeliveryStore().loadDurable()
  expect(state.queue[id]).toMatchObject({ id, unknownResult: { preserved: true }, attributes: legacy.attributes })
  expect(state.deadLetters).toHaveLength(2)
  expect(state.foldState?.open[7]).toMatchObject({ id, revision: Date.parse(legacy.endTime),
    lastSnapshot: { id, endTime: legacy.endTime }, activityKey: legacy.identityKey })
  expect(state.foldState?.open[7].kind).toBeUndefined()
  f.sessionArea.values = {}
  expect(await new ChromeBrowserDeliveryStore().loadDurable()).toEqual(state)
})

it('keeps every original key on failed migration, retries once and rejects the previous real loader after upgrade', async () => {
  const f = fixture({ pendingSegments: { [id]: legacy } }, { foldState: legacyFold })
  const before = structuredClone(f.localArea.values)
  vi.spyOn(f.localArea, 'set').mockRejectedValueOnce(new Error('simulated quota failure'))
  await expect(new ChromeBrowserDeliveryStore().loadDurable()).rejects.toThrow('simulated quota failure')
  expect(f.localArea.values).toEqual(before)
  expect(f.sessionArea.values.foldState).toEqual(legacyFold)
  const state = await new ChromeBrowserDeliveryStore().loadDurable()
  const upgraded = structuredClone(f.localArea.values)
  await expect(new PreviousStore().loadDurable()).rejects.toThrow()
  expect(f.localArea.values).toEqual(upgraded)
  expect(await new ChromeBrowserDeliveryStore().loadDurable()).toEqual(state)
})

it.each([null, [], 'unsupported encoding'])('refuses an unreadable historical queue instead of checkpointing an empty queue: %j', async queue => {
  const f = fixture({ pendingSegments: queue, browserCollectorPendingGap: {
    start: legacy.startTime, end: legacy.endTime, reason: 'buffer_overflow', estimatedFactsLost: 1 } })
  const before = structuredClone(f.localArea.values)
  await expect(new ChromeBrowserDeliveryStore().loadDurable()).rejects.toThrow('preserve')
  expect(f.localArea.values).toEqual(before)
})

it('checkpoints real fold transitions and pending snapshots as one durable commit across worker restart and failed close', async () => {
  const f = fixture()
  const store = new ChromeBrowserDeliveryStore()
  const delivery = createBrowserDelivery({ store,
    hub: { findCompatibleHub: async () => null, deliverProtocol: async () => { throw new Error('offline') } },
    loadAppIdentityKey: async () => 'win:msedge', loadBasePort: async () => 24820, loadExternalHostIdentity: async () => id })
  const deps: FoldDeps = { segments: createSegmentSdk({ source: 'browser', payloadOf: browserPayloadOf }), activityKeyOf }
  const started = applyEvent(emptyState(), { kind: 'activated', windowId: 7, at: 1_000, url: legacy.attributes.url, title: 'One' }, deps)
  await delivery.checkpoint(started.out, started.state)
  const observed = flush(started.state, 2_000, deps)
  await delivery.checkpoint(observed.out, observed.state)
  const beforeClose = await new ChromeBrowserDeliveryStore().loadDurable()
  const closed = applyEvent(beforeClose.foldState!, { kind: 'windowClosed', windowId: 7, at: 2_000 }, deps)
  vi.spyOn(f.localArea, 'set').mockRejectedValueOnce(new Error('disk full'))
  await expect(delivery.checkpoint(closed.out, closed.state)).rejects.toThrow('disk full')
  expect(await new ChromeBrowserDeliveryStore().loadDurable()).toEqual(beforeClose)
  await delivery.checkpoint(closed.out, closed.state)
  f.sessionArea.values = {}
  const restarted = await new ChromeBrowserDeliveryStore().loadDurable()
  expect(restarted.foldState?.open).toEqual({})
  expect(Object.values(restarted.queue)).toMatchObject([{ id: observed.out[0].id, kind: 'segment', isFinal: true, revision: 2 }])
})
