import { describe, expect, it } from 'vitest'
import { clampRange, groupApplications, groupSubjects, presentFact, relatedBrowser, zoomRange, type ExperienceSegment } from './factViews'

export function fact(overrides: Partial<ExperienceSegment> = {}): ExperienceSegment {
  return { id: 'one', streamId: 'stream', factId: 'fact', revision: 1, subjectId: 'machine',
    subjectKind: 'machine', subjectName: 'Mac', source: 'system', appId: 1, appIdentityId: 1, appName: 'Browser', appKey: 'browser',
    startTime: '2026-09-09T01:00:00Z', endTime: '2026-09-09T01:00:01Z', payload: {}, ...overrides }
}
describe('Fact Views', () => {
  it('preserves every short fact and only groups subjects with overlapping activity', () => {
    const a = fact(), b = fact({ id: 'two' }), c = fact({ id: 'outside', subjectId: 'account', startTime: '2026-09-09T02:00:00Z', endTime: '2026-09-09T03:00:00Z' })
    const groups = groupSubjects([a, b, c], { start: Date.parse(a.startTime), end: Date.parse(a.endTime) })
    expect(groups).toHaveLength(1)
    expect(groups[0].facts).toEqual([a, b])
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
    expect(groupSubjects([point], { start: Date.parse(point.startTime), end: Date.parse(point.startTime) + 1000 })).toHaveLength(1)
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
