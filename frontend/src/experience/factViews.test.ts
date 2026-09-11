import { expect, it } from 'vitest'
import { clampRange, groupApplications, groupObjects, rangeOf, presentFact, relatedPages, zoomRange, type ExperienceSegment } from './factViews'

const machine = { id: 'mac', kind: 'machine', scope: 'heartbeat.device', key: 'Mac', name: 'Mac' }
const chrome = { id: 'chrome', kind: 'app', scope: 'heartbeat.app', key: 'chrome', name: 'Chrome' }
function fact(overrides: Partial<ExperienceSegment> = {}): ExperienceSegment {
  return { id: 'one', streamId: 'stream', factId: 'fact', revision: 1, foi: machine,
    relations: [{ id: 'r', kind: 'observed-on', validFrom: null, validTo: null, evidence: { factId: 'one' },
      members: [{ role: 'device', object: machine }, { role: 'app', object: chrome }] }],
    source: 'new.desktop', aspect: 'desktop-activity', appId: null, appIdentityId: null, appName: null, appKey: null,
    startTime: '2026-09-09T01:00:00Z', endTime: '2026-09-09T01:00:01Z', payload: {}, ...overrides }
}

it('groups by object UUID without requiring device or product metadata, preserving overlapping facts', () => {
  const desktop = fact(), independent = fact({ id: 'two', collectorId: 'another' })
  const page = fact({ id: 'page', foi: chrome, source: 'new.page', aspect: 'selected-page' })
  const windows = fact({ ...page, id: 'windows-page', relations: [{ ...page.relations![0],
    members: [{ role: 'device', object: { ...machine, id: 'windows' } }, { role: 'app', object: chrome }] }] })
  const groups = groupObjects([desktop, independent, page, windows], rangeOf(desktop))
  expect(groups.find(g => g.id === machine.id)?.facts).toEqual([desktop, independent])
  expect(groups.find(g => g.id === chrome.id)?.facts).toEqual([page, windows])
  expect(relatedPages(desktop, [page, windows])).toEqual([page])
  expect(groupApplications([desktop, independent, page])[0]).toMatchObject({ id: 'mac/chrome', name: 'Chrome', facts: [desktop, independent] })
})

it('does not infer relations from identical names, numeric metadata, or neighboring times', () => {
  const desktop = fact({ deviceId: 1, appId: 1 })
  const page = fact({ foi: chrome, aspect: 'selected-page', deviceId: 1, appId: 1 })
  const unlinked = { ...page, relations: [] }
  const wrongApp = { ...page, foi: { ...chrome, id: 'other-app' } }
  const adjacent = { ...page, startTime: desktop.endTime, endTime: '2026-09-09T01:00:02Z' }
  expect(relatedPages(desktop, [unlinked, wrongApp, adjacent])).toEqual([])
  expect(relatedPages(fact({ relations: [] }), [page])).toEqual([])
})

it('keeps account and unknown histories readable without turning the stream into an object', () => {
  const account = fact({ foi: { id: 'account', kind: 'account', scope: 'world', key: 'user', name: null },
    aspect: 'account-location', relations: [] })
  expect(groupObjects([account], rangeOf(account))[0]).toMatchObject({ id: 'account', name: 'user', kind: 'account' })
  expect(groupApplications([account])).toEqual([])
  expect(relatedPages(account, [account])).toEqual([])
  const unknown = fact({ foi: null, relations: [] })
  expect(groupObjects([unknown], rangeOf(unknown))[0]).toMatchObject({ name: '未知对象', kind: 'unknown' })
  expect(relatedPages(unknown, [unknown])).toEqual([])
})

it('chooses display by Aspect, preserves opaque JSON, and rejects unsafe links', () => {
  expect(presentFact(fact({ appKey: 'away' })).title).toBe('离开')
  expect(presentFact(fact({ appKey: 'browser', appName: '__away__', payload: { title: 'Original' } })).title).toBe('Original')
  expect(presentFact(fact({ source: 'other.account', aspect: 'account-location', payload: { worldName: 'Quiet World' } }))).toMatchObject({ title: 'Quiet World', subtitle: '账号位置' })
  for (const payload of [null, [], 'text', { title: 'not a declared title' }])
    expect(presentFact(fact({ source: 'system', aspect: 'custom', payload })).title).toBe('原始观察')
  expect(presentFact(fact({ aspect: '__proto__' })).title).toBe('原始观察')
  const page = (url: string) => presentFact(fact({ source: 'other.page', aspect: 'selected-page', payload: { attributes: { url } } }))
  expect(page('https://example.com').fields[0].href).toBe('https://example.com/')
  expect(page('javascript:alert(1)').fields[0].href).toBeUndefined()
})

it('retains zero-length snapshots and excludes out-of-window objects', () => {
  const point = fact({ endTime: '2026-09-09T01:00:00Z' })
  const outside = fact({ startTime: '2026-09-09T02:00:00Z', endTime: '2026-09-09T03:00:00Z' })
  expect(groupObjects([point, outside], { start: Date.parse(point.startTime), end: Date.parse(point.startTime) + 1000 })[0].facts).toEqual([point])
})

it('zooms around the pointer and pans within a DST-length day', () => {
  const bounds = { start: 0, end: 23 * 3600_000 }
  expect(zoomRange({ start: 10_000, end: 20_000 }, bounds, .5, .2)).toEqual({ start: 11_000, end: 16_000 })
  expect(clampRange({ start: -1000, end: 5000 }, bounds)).toEqual({ start: 0, end: 6000 })
  expect(zoomRange(bounds, bounds, 2)).toEqual(bounds)
  expect(clampRange({ start: bounds.end - 100, end: bounds.end + 900 }, bounds)).toEqual({ start: bounds.end - 1000, end: bounds.end })
})
