import { afterEach, expect, it, vi } from 'vitest'
import { ChromeBrowserDeliveryStore } from '../src/delivery-chrome'

class StorageArea {
  values: Record<string, unknown> = {}
  async get(keys: string | string[]) {
    return structuredClone(Object.fromEntries((typeof keys === 'string' ? [keys] : keys)
      .filter(key => key in this.values).map(key => [key, this.values[key]])))
  }
  async set(items: Record<string, unknown>) { Object.assign(this.values, structuredClone(items)) }
  async remove(keys: string | string[]) {
    for (const key of typeof keys === 'string' ? [keys] : keys) delete this.values[key]
  }
}
type Tab = { id: number; windowId: number; active: boolean; url: string; title: string }
function event<T extends unknown[]>() {
  let handler: (...args: T) => void
  return { addListener: (value: typeof handler) => { handler = value }, fire: (...args: T) => handler(...args) }
}
function fixture() {
  const local = new StorageArea(), session = new StorageArea()
  const tabs: Tab[] = [1, 2].map(windowId => ({ id: windowId, windowId, active: true,
    url: 'https://example.com/shared', title: 'Shared page' }))
  let at = 1_000
  vi.spyOn(Date, 'now').mockImplementation(() => at)
  vi.stubEnv('MODE', 'production')
  vi.stubGlobal('navigator', { userAgentData: { brands: [{ brand: 'Google Chrome' }] }, userAgent: 'Chrome/130' })
  vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('fixture offline') }))
  async function worker() {
    vi.resetModules()
    const activated = event<[{ tabId: number; windowId: number }]>(), updated = event<[number, { title: string }, Tab]>(),
      removed = event<[number]>(), alarm = event<[{ name: string }]>()
    const query = vi.fn(async () => structuredClone(tabs))
    vi.stubGlobal('chrome', { storage: { local, session },
      runtime: { getPlatformInfo: async () => ({ os: 'mac' }), getURL: (path: string) => `chrome-extension://fixture/${path}` },
      tabs: { query, get: async (id: number) => structuredClone(tabs.find(tab => tab.id === id)), onActivated: activated, onUpdated: updated },
      windows: { onRemoved: removed }, alarms: { create: vi.fn(), onAlarm: alarm } })
    await import('../src/background')
    return { activated, updated, removed, alarm, query }
  }
  return { local, session, tabs, worker, at: (value: number) => { at = value } }
}
const durable = () => new ChromeBrowserDeliveryStore().loadDurable()
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.unstubAllEnvs() })

it('runs real worker listeners for overlapping same-page windows, title-only revisions, close/reopen and worker restart', async () => {
  const f = fixture()
  let worker = await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  f.at(2_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(async () => expect(Object.values((await durable()).queue)).toHaveLength(2))
  await vi.waitFor(() => expect(worker.query).toHaveBeenCalledTimes(2))
  const original = await durable(), firstId = original.foldState!.open[1].id, secondId = original.foldState!.open[2].id
  expect(firstId).not.toBe(secondId)
  expect(Object.values(original.queue).map(snapshot => snapshot.attributes.windowId).sort()).toEqual([1, 2])

  worker = await f.worker()
  await vi.waitFor(() => expect(worker.query).toHaveBeenCalledOnce())
  f.tabs[0].title = 'Changed without advancing end time'
  worker.updated.fire(1, { title: f.tabs[0].title }, f.tabs[0])
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(async () => expect((await durable()).queue[firstId]).toMatchObject({
    title: f.tabs[0].title, revision: 2, endTime: '1970-01-01T00:00:02.000Z' }))
  f.tabs.splice(0, 1)
  worker.removed.fire(1)
  await vi.waitFor(async () => expect((await durable()).queue[firstId]).toMatchObject({ isFinal: true, revision: 3 }))
  expect((await durable()).foldState!.open[2].id).toBe(secondId)
  f.tabs.push({ id: 3, windowId: 1, active: true, url: 'https://example.com/shared', title: 'Reopened' })
  worker.activated.fire({ tabId: 3, windowId: 1 })
  await vi.waitFor(async () => expect((await durable()).foldState!.open[1]?.id).toBeDefined())
  expect((await durable()).foldState!.open[1].id).not.toBe(firstId)
})

it('ends prior browser-session facts at the last known snapshot and starts current windows after a browser restart', async () => {
  const f = fixture(), worker = await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  f.at(2_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(() => expect(worker.query).toHaveBeenCalledTimes(2))
  const previous = Object.values((await durable()).queue)
  f.session.values = {}
  f.at(10_000)
  const restarted = await f.worker()
  await vi.waitFor(() => expect(restarted.query).toHaveBeenCalledOnce())
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  const state = await durable()
  for (const snapshot of previous) {
    expect(state.queue[snapshot.id]).toMatchObject({ id: snapshot.id, endTime: snapshot.endTime, isFinal: true, revision: 2 })
    expect(Object.values(state.foldState!.open).some(activity => activity.id === snapshot.id)).toBe(false)
  }
  expect(Object.values(state.foldState!.open).map(activity => activity.startTime)).toEqual([10_000, 10_000])
})

it('retains legacy fold without an ACK high-water snapshot for Runtime recovery and does not grow it offline', async () => {
  const f = fixture(), oldId = '0198d5eb-fc31-7d7b-8bf0-000000000001'
  f.session.values.foldState = { open: { 1: { ...f.tabs[0], id: oldId, startTime: 0, identityKey: f.tabs[0].url } } }
  const worker = await f.worker()
  await vi.waitFor(async () => expect((await durable()).foldState!.open[1]).toMatchObject({ id: oldId, recoveryRequired: true }))
  f.at(2_000)
  f.tabs.splice(0, 1)
  worker.removed.fire(1)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(() => expect(worker.query).toHaveBeenCalledTimes(2))
  const state = await durable()
  expect(state.foldState!.open[1]).toMatchObject({ id: oldId, recoveryRequired: true, startTime: 0 })
  expect(state.queue[oldId]).toBeUndefined()
})

it('finalizes a newly opened unflushed fact at its known start on full browser restart', async () => {
  const f = fixture()
  await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  const previous = Object.values((await durable()).foldState!.open)
  f.session.values = {}
  f.at(10_000)
  await f.worker()
  await vi.waitFor(async () => {
    const state = await durable()
    for (const activity of previous) expect(state.queue[activity.id]).toMatchObject({
      id: activity.id, startTime: '1970-01-01T00:00:01.000Z', endTime: '1970-01-01T00:00:01.000Z',
      isFinal: true, revision: 1,
    })
    expect(Object.values(state.foldState!.open).map(activity => activity.startTime)).toEqual([10_000, 10_000])
  })
})

it('a disabled worker preserves unresolved legacy responsibility and accurately finalizes known snapshots', async () => {
  const f = fixture(), worker = await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  f.at(2_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(() => expect(worker.query).toHaveBeenCalledTimes(2))
  const previous = await durable(), first = previous.foldState!.open[1], waitingId = '0198d5eb-fc31-7d7b-8bf0-000000000001'
  await new ChromeBrowserDeliveryStore().saveDurable({ ...previous, policy: { ...previous.policy, enabled: false },
    foldState: { open: { ...previous.foldState!.open, 3: { ...first, id: waitingId, windowId: 3,
      kind: undefined, revision: undefined, lastSnapshot: undefined, recoveryRequired: true } } } })
  f.at(10_000)
  await f.worker()
  await vi.waitFor(async () => {
    const state = await durable()
    expect(Object.keys(state.foldState!.open)).toEqual(['3'])
    expect(state.foldState!.open[3]).toMatchObject({ id: waitingId, recoveryRequired: true })
    expect(state.queue[first.id]).toMatchObject({ endTime: '1970-01-01T00:00:02.000Z', isFinal: true, revision: 2 })
  })
})

it('keeps trying delivery when a full outbox cannot checkpoint another open window', async () => {
  const f = fixture(), worker = await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  const before = await durable()
  const queued = fullOutbox()
  await new ChromeBrowserDeliveryStore().saveDurable({ ...before, queue: queued })
  f.at(2_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(() => expect(fetch).toHaveBeenCalled())
  const after = await durable()
  expect(after.queue).toEqual(queued)
  expect(after.foldState).toEqual(before.foldState)
  expect(after.pendingGaps).toEqual([])
  // Bring the exact protocol boundary online. Existing custody must drain before the new
  // activity can be checkpointed, then that same producer identity must actually get ACKed.
  const acknowledged = connectFixtureHub()
  f.at(32_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(async () => expect(Object.keys((await durable()).queue)).toHaveLength(4500))
  f.at(62_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  const id = before.foldState!.open[1].id
  await vi.waitFor(async () => expect((await durable()).queue[id]?.id).toBe(id))
  for (let cycle = 0; cycle < 10 && !acknowledged.includes(id); cycle++) {
    const queries = worker.query.mock.calls.length
    worker.alarm.fire({ name: 'heartbeat-flush' })
    await vi.waitFor(() => expect(worker.query.mock.calls.length).toBeGreaterThan(queries))
  }
  await vi.waitFor(() => expect(acknowledged).toContain(id))
  expect((await durable()).foldState!.open[1].id).toBe(id)
})

function connectFixtureHub() {
  const activationId = '0198d5e8-30cb-7d54-bab1-250087147e4c'
  const streamId = '0198d5e2-e0d4-7b30-9da7-342ee261bf62'
  const acknowledged: string[] = []
  const response = (type: string, body: object, request?: { messageId: string }, activation: string | undefined = activationId) => Response.json({
    protocol: type === 'activation.accepted' ? 'heartbeat.collector.bootstrap/1' : 'heartbeat.collector/1',
    type, messageId: '0198d5e8-30cc-743c-a3d6-ac61956f26b5', activationId: activation || undefined, replyTo: request?.messageId, body,
  })
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    if (url.startsWith('chrome-extension://')) return Response.json({ packageId: 'heartbeat.collector.browser', packageVersion: '0.1.0',
      packageContentHash: `sha256:${'1'.repeat(64)}`, artifactId: 'browser.extension', artifactHash: `sha256:${'2'.repeat(64)}` })
    const request = JSON.parse(String(init?.body ?? '{}'))
    if (url.endsWith('/external-host')) return Response.json({ binding: 'external-host', protocolMajors: [1] })
    if (url.endsWith('/hello')) return response('activation.accepted', { activationId, selectedProtocolMajor: 1,
      selectedCapabilities: { 'facts.observation': 2, 'facts.aspect': 1, 'facts.segment': 1, 'diagnostics.stream-gap': 1 } }, request, '')
    if (url.endsWith('/initialize')) return response('activation.initialize', {
      instance: { subject: { kind: 'machine', subjectId: '02a8259d-5f6a-4b83-b6ba-87017886319e' } },
      spec: { revision: 1, config: { value: { enabled: true, flushPeriodMs: 30_000 } } },
      limits: { maxFactsPerBatch: 500, maxBatchBytes: 1_048_576 },
    })
    if (url.endsWith('/initialized')) return Response.json({})
    if (url.endsWith('/streams')) return response('streams.opened', { streams: { tabs: { streamId } } }, request)
    if (url.endsWith('/ready')) return response('activation.readyAck', { lease: { token: 'lease', expiresAt: '2026-09-11T12:00:00Z' } }, request)
    if (url.endsWith('/renew')) return Response.json({ token: 'lease', expiresAt: '2026-09-11T12:00:00Z' })
    if (url.endsWith('/facts')) {
      acknowledged.push(...request.body.facts.map((fact: { factId: string }) => fact.factId))
      return response('facts.ack', { results: request.body.facts.map((_: unknown, index: number) => ({ index, status: 'committed' })) }, request)
    }
    throw new Error(`Unexpected fixture request ${url}`)
  }))
  return acknowledged
}

it('a transient checkpoint write failure leaves fold unchanged and retries the same fact after delivering', async () => {
  const f = fixture(), worker = await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  const before = await durable(), id = before.foldState!.open[1].id
  const acknowledged = connectFixtureHub()
  vi.spyOn(f.local, 'set').mockRejectedValueOnce(new Error('temporary storage failure'))
  f.at(2_000)
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(() => expect(worker.query).toHaveBeenCalledTimes(2))
  expect((await durable()).foldState).toEqual(before.foldState)
  expect((await durable()).queue).toEqual({})
  worker.alarm.fire({ name: 'heartbeat-flush' })
  await vi.waitFor(() => expect(acknowledged).toContain(id))
  expect((await durable()).foldState!.open[1]).toMatchObject({ id, revision: 1 })
})

function fullOutbox() {
  return Object.fromEntries(Array.from({ length: 5000 }, (_, index) => {
    const id = `0198d5eb-fc31-7d7b-8bf0-${index.toString(16).padStart(12, '0')}`
    return [id, { id, kind: 'segment' as const, revision: 1, source: 'browser' as const, isFinal: true,
      startTime: '1970-01-01T00:00:01.000Z', endTime: '1970-01-01T00:00:02.000Z',
      activityKey: 'https://example.com/old', title: 'Saved',
      attributes: { windowId: 7, url: 'https://example.com/old', domain: 'example.com', site: 'example.com' } }]
  }))
}

it('full browser restart can drain a full queue before checkpointing known-end finals', async () => {
  const f = fixture()
  await f.worker()
  await vi.waitFor(async () => expect(Object.keys((await durable()).foldState!.open)).toHaveLength(2))
  const previous = await durable(), ids = Object.values(previous.foldState!.open).map(activity => activity.id)
  await new ChromeBrowserDeliveryStore().saveDurable({ ...previous, queue: fullOutbox() })
  f.session.values = {}
  f.at(32_000)
  connectFixtureHub()
  const restarted = await f.worker()
  await vi.waitFor(() => expect(restarted.query).toHaveBeenCalledOnce())
  const after = await durable()
  expect(Object.keys(after.queue)).toHaveLength(4502)
  for (const id of ids) expect(after.queue[id]).toMatchObject({ id, isFinal: true, revision: 1,
    startTime: '1970-01-01T00:00:01.000Z', endTime: '1970-01-01T00:00:01.000Z' })
  expect(Object.values(after.foldState!.open).every(activity => !ids.includes(activity.id) && activity.startTime === 32_000)).toBe(true)
})
