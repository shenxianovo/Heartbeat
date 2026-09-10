import { describe, expect, it } from 'vitest'
import { clampRange, groupApplications, groupTargets, rangeOf, presentFact, relatedBrowser, zoomRange, type ExperienceSegment } from './factViews'

export function fact(overrides: Partial<ExperienceSegment> = {}): ExperienceSegment {
  return { id: 'one', streamId: 'stream', factId: 'fact', revision: 1, subjectId: 'machine',
    subjectKind: 'machine', subjectName: 'Mac', source: 'system', appId: 1, appIdentityId: 1, appName: 'Browser', appKey: 'browser',
    startTime: '2026-09-09T01:00:00Z', endTime: '2026-09-09T01:00:01Z', payload: {}, ...overrides }
}
it('relates Browser application contexts to System by device and App while preserving separate Target lanes', () => {
  const system = fact({ source: 'system', deviceId: 42, appId: 9, appIdentityId: 11, targetKind: 'device', targetId: 42 })
  const browser = fact({ source: 'browser', deviceId: 42, appId: 9, appIdentityId: 12, targetKind: 'application-context', targetId: 81 })
  const unrelated = { ...browser, id: 'other', deviceId: 43 }
  expect(relatedBrowser(system, [browser, unrelated])).toEqual([browser])
  expect(groupTargets([system, browser], { start: Date.parse(system.startTime), end: Date.parse(system.endTime) })).toHaveLength(2)
})

describe('Fact Views', () => {
  it('keeps independent account observations in their account lane without adding device attention', () => {
    const account = fact({ id: 'one', source: 'vrchat.account', observerId: 'observer-one',
      targetKind: 'account', targetId: 71, targetName: 'usr_account', deviceId: null,
      appId: 9, appName: 'VRChat', appKey: 'vrchat', appIdentityId: null })
    const second = { ...account, id: 'two', observerId: 'observer-two' }
    const groups = groupTargets([account, second], rangeOf(account))
    expect(groups).toHaveLength(1)
    expect(groups[0]).toMatchObject({ id: 'account:71', name: 'usr_account', kind: 'account', facts: [account, second] })
    expect(groupApplications([account, second])).toEqual([])
    expect(relatedBrowser(account, [second])).toEqual([])
  })
  it('preserves every short fact and only groups subjects with overlapping activity', () => {
    const a = fact(), b = fact({ id: 'two' }), c = fact({ id: 'outside', subjectId: 'account', startTime: '2026-09-09T02:00:00Z', endTime: '2026-09-09T03:00:00Z' })
    const groups = groupTargets([a, b, c], { start: Date.parse(a.startTime), end: Date.parse(a.endTime) })
    expect(groups).toHaveLength(1)
    expect(groups[0].facts).toEqual([a, b])
  })
  it('groups System by direct device Target and correlates legacy Browser on that device', () => {
    const system = fact({ observerId: 'observer-one', targetKind: 'device', targetId: 42, targetName: 'Desktop',
      deviceId: 42, subjectId: undefined, subjectKind: undefined, subjectName: undefined })
    const independent = fact({ ...system, id: 'independent', observerId: 'observer-two' })
    const browser = fact({ id: 'browser', source: 'browser', deviceId: 42, subjectId: 'old-machine' })
    const wrongDevice = fact({ id: 'wrong-device', source: 'browser', deviceId: 43 })
    const groups = groupTargets([system, independent, browser, wrongDevice],
      { start: Date.parse(system.startTime), end: Date.parse(system.endTime) })
    expect(groups.find(g => g.id === 'device:42')).toMatchObject({ name: 'Desktop', kind: 'device', facts: [system, independent, browser] })
    expect(groupApplications([system, independent])).toHaveLength(1)
    expect(relatedBrowser(system, [browser, wrongDevice])).toEqual([browser])
  })
  it('expands applications within devices without joining adjacent facts or confusing product names', () => {
    const a = fact(), b = fact({ id: 'adjacent', startTime: a.endTime, endTime: '2026-09-09T01:00:02Z', appIdentityId: 2 })
    const otherDevice = fact({ id: 'device2', subjectId: 'other' })
    const sameName = fact({ id: 'other-app', appId: 2 })
    const groups = groupApplications([a, b, otherDevice, sameName, fact({ source: 'browser' })])
    expect(groups).toHaveLength(3)
    expect(groups.find(g => g.id === 'machine/app:1')?.facts).toEqual([a, b])
    expect(groups.flatMap(g => g.facts)).toHaveLength(4)
    expect(a.endTime).toBe(b.startTime)
  })
  it('keeps all related browser candidates, matching AppIdentity rather than product name', () => {
    const system = fact()
    const a = fact({ id: 'a', source: 'browser' }), b = fact({ id: 'b', source: 'browser' })
    const wrongSubject = fact({ source: 'browser', subjectId: 'other' })
    const wrongIdentity = fact({ source: 'browser', appIdentityId: 2 })
    const touchesEnd = fact({ source: 'browser', startTime: system.endTime, endTime: '2026-09-09T02:00:00Z' })
    expect(relatedBrowser(system, [a, b, wrongSubject, wrongIdentity, touchesEnd])).toEqual([a, b])
    expect(relatedBrowser(fact({ appIdentityId: null }), [a])).toEqual([])
  })
  it('keeps zero-length snapshots at the start of the visible window', () => {
    const point = fact({ endTime: '2026-09-09T01:00:00Z' })
    expect(groupTargets([point], { start: Date.parse(point.startTime), end: Date.parse(point.startTime) + 1000 })).toHaveLength(1)
  })
  it('renders app-less worlds and unknown JSON without inventing field meaning or unsafe links', () => {
    expect(presentFact(fact({ appKey: 'away' })).title).toBe('离开')
    expect(presentFact(fact({ appKey: 'browser', appName: '__away__', payload: { title: 'Original title' } })).title).toBe('Original title')
    expect(presentFact(fact({ source: 'vrchat.account', appIdentityId: null, payload: { worldName: 'Quiet World', instanceId: 'i' } })).title).toBe('Quiet World')
    for (const payload of [null, [], 'text', { title: 'not a declared title' }])
      expect(presentFact(fact({ source: 'custom', payload })).title).toBe('原始观察')
    const browser = presentFact(fact({ source: 'browser', payload: { attributes: { url: 'javascript:alert(1)' } } }))
    expect(browser.fields[0].href).toBeUndefined()
    expect(presentFact(fact({ source: '__proto__' })).title).toBe('原始观察')
  })
  it('zooms around the pointer and pans within a DST-length day without changing the span', () => {
    const bounds = { start: 0, end: 23 * 3600_000 }
    expect(zoomRange({ start: 10_000, end: 20_000 }, bounds, .5, .2)).toEqual({ start: 11_000, end: 16_000 })
    expect(clampRange({ start: -1000, end: 5000 }, bounds)).toEqual({ start: 0, end: 6000 })
    expect(zoomRange(bounds, bounds, 2)).toEqual(bounds)
    expect(clampRange({ start: bounds.end - 100, end: bounds.end + 900 }, bounds)).toEqual({ start: bounds.end - 1000, end: bounds.end })
  })
})
