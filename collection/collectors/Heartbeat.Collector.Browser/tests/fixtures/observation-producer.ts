import assert from 'node:assert/strict'
import { readFileSync, writeFileSync } from 'node:fs'
import { applyEvent, browserPayloadOf, emptyState, flush, type FoldState, type SegmentSnapshot } from '../../src/fold'
import { createSegmentSdk } from '../../src/sdk/segments'
import { activityKeyOf } from '../../src/normalize'
import { uploadWithBrowserProtocol, reportBrowserGap } from '../../src/protocol'
import { uuidv7 } from '../../src/ids'
import { ChromeBrowserDeliveryStore } from '../../src/delivery-chrome'
import { createBrowserDelivery } from '../../src/delivery'
import { LoopbackBrowserHubAdapter } from '../../src/hub'

const [portText, packageFile, checkpointPath, phase] = process.argv.slice(2)
const port = Number(portText)
const reference = JSON.parse(readFileSync(packageFile, 'utf8'))
const observer = '6a8259d1-5f6a-4b83-b6ba-870178863199'
const originalFetch = globalThis.fetch
Object.assign(globalThis, { chrome: { runtime: { getURL: (path: string) => `chrome-extension://fixture/${path}` } } })
globalThis.fetch = (input, init) => String(input).startsWith('chrome-extension://fixture/')
  ? Promise.resolve(Response.json(reference)) : originalFetch(input, init)
const deps = { segments: createSegmentSdk({ source: 'browser', payloadOf: browserPayloadOf }), activityKeyOf }
const url = 'https://example.com/browser-observation'
let state: FoldState
let start: number
let snapshots: SegmentSnapshot[]
if (phase === 'recover') {
  const previous = JSON.parse(readFileSync(checkpointPath, 'utf8'))
  // The historical extension ACK removed pendingSegments, while foldState had no revision.
  // Feed those real storage keys to the production Chrome migration and delivery adapter.
  const local: Record<string, unknown> = { browserObservation: previous.attribution, browserCollectorExternalHostIdentity: observer, pendingObservationFacts: {} }
  const session: Record<string, unknown> = { foldState: previous.state }
  const storage = (data: Record<string, unknown>) => ({
    async get(keys: string | string[]) { return Object.fromEntries((Array.isArray(keys) ? keys : [keys]).filter(key => data[key] !== undefined).map(key => [key, structuredClone(data[key])])) },
    async set(values: Record<string, unknown>) { Object.assign(data, structuredClone(values)) },
    async remove(keys: string | string[]) { for (const key of Array.isArray(keys) ? keys : [keys]) delete data[key] },
  })
  Object.assign(chrome, { storage: { local: storage(local), session: storage(session) } })
  const store = new ChromeBrowserDeliveryStore()
  const delivery = createBrowserDelivery({ store, hub: new LoopbackBrowserHubAdapter(), loadBasePort: async () => port,
    loadAppIdentityKey: async () => 'mac:com.google.Chrome', loadExternalHostIdentity: async () => observer })
  await delivery.deliveryCycle()
  const recovered = await store.loadDurable()
  const final = Object.values(recovered.queue)[0]
  assert.ok(final, 'Runtime recovery must checkpoint the exact legacy final')
  assert.equal(final.id, previous.snapshots[0].id)
  assert.equal(Date.parse(final.endTime), Date.parse(previous.snapshots[0].endTime))
  assert.equal(final.revision, previous.snapshots[0].revision + 1)
  assert.equal(final.isFinal, true)
  assert.equal(final.kind, undefined)
  await delivery.deliveryCycle()
  assert.equal(Object.keys((await store.loadDurable()).queue).length, 0)
  writeFileSync(checkpointPath, JSON.stringify({ ...previous, recovered: final }))
  console.log('Browser legacy ACK recovery: original identity and known end finalized')
  process.exit(0)
}
if (phase === 'legacy-start') {
  start = Date.now() - 60_000
  state = { open: { 44: { id: uuidv7(), startTime: start, windowId: 44, activityKey: url, url, title: 'Legacy ACKed title' } } }
  snapshots = flush(state, start + 20_000, deps).out
} else if (phase === 'start') {
  start = Date.now() - 60_000
  state = emptyState()
  for (const windowId of [11, 22]) {
    state = applyEvent(state, { kind: 'activated', windowId, url, title: 'Initial title', at: start }, deps).state
  }
  const result = flush(state, start + 20_000, deps)
  state = result.state
  snapshots = result.out
} else {
  const previous = JSON.parse(readFileSync(checkpointPath, 'utf8'))
  state = previous.state
  start = previous.start
  if (phase === 'replay') {
    snapshots = previous.snapshots
  } else {
    // Same EndTime with changed content, followed by a legitimate shorter terminal snapshot.
    state = applyEvent(state, { kind: 'activated', windowId: 11, url, title: 'Revised title', at: start + 20_000 }, deps).state
    const revised = flush(state, start + 20_000, deps)
    state = revised.state
    const published = await uploadWithBrowserProtocol(port, 'mac:com.google.Chrome', observer, revised.out)
    assert.equal(published.kind, 'acked', JSON.stringify(published))
    const closed = applyEvent(state, { kind: 'windowClosed', windowId: 11, at: start + 15_000 }, deps)
    state = closed.state
    snapshots = closed.out
    // Reopening the same window and page must create a new independent Fact.
    state = applyEvent(state, { kind: 'activated', windowId: 11, url, title: 'Reopened', at: start + 25_000 }, deps).state
    const reopened = flush(state, start + 30_000, deps)
    state = reopened.state
    snapshots.push(...reopened.out)
  }
}
const published = await uploadWithBrowserProtocol(port, 'mac:com.google.Chrome', observer, snapshots)
assert.equal(published.kind, 'acked', JSON.stringify(published))
if (published.kind !== 'acked') throw new Error('Unreachable')
assert.equal(published.acknowledgedIds.length, snapshots.length)
if (phase === 'revise') {
  assert.equal(await reportBrowserGap(published.session, {
    gapId: uuidv7(), start: new Date(start + 40_000).toISOString(), end: new Date(start + 41_000).toISOString(),
    reason: 'buffer_overflow', estimatedFactsLost: 1,
  }), 'acked')
}
writeFileSync(checkpointPath, JSON.stringify({ state, start, snapshots, observer, attribution: published.session.attribution }))
console.log(`Browser production ${phase}: ${snapshots.length} snapshots acknowledged`)
