import { isAwayApp } from '../appLabels'

export interface ExperienceSegment {
  id: string
  streamId: string
  factId: string
  revision: number
  subjectId: string
  subjectKind: string
  subjectName: string | null
  source: string
  appId: number | null
  appIdentityId: number | null
  appName: string | null
  appKey: string | null
  startTime: string
  endTime: string
  payload: unknown
}

export interface TimeRange { start: number; end: number }
export interface FactPresentation {
  title: string
  subtitle: string
  color: string
  fields: { label: string; value: string; href?: string }[]
}

function object(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown> : {}
}
function text(value: unknown): string { return typeof value === 'string' ? value : '' }
function safeUrl(value: string): string | undefined {
  try { const url = new URL(value); return ['https:', 'http:'].includes(url.protocol) ? url.href : undefined }
  catch { return undefined }
}

type FactView = (fact: ExperienceSegment, payload: Record<string, unknown>) => FactPresentation
// Developer-authored views share raw facts; collectors do not deliver executable presentation code.
const views: Record<string, FactView> = {
  system: (fact, p) => isAwayApp(fact.appKey, fact.appName) ? {
    title: '离开', subtitle: '系统记录的离开区间', color: '#8893a1', fields: [],
  } : ({
    title: text(p.title) || fact.appName || '前台活动',
    subtitle: fact.appName || 'System', color: '#388bb5', fields: [],
  }),
  browser: (fact, p) => {
    const attrs = object(p.attributes)
    const url = text(attrs.url)
    return {
      title: text(p.title) || text(attrs.site) || '浏览器页面',
      subtitle: text(attrs.site) || text(attrs.domain) || fact.appName || 'Browser',
      color: '#bd8843',
      fields: url ? [{ label: 'URL', value: url, href: safeUrl(url) }] : [],
    }
  },
  'vrchat.account': (_fact, p) => ({
    title: text(p.worldName) || text(p.title) || text(p.worldId) || 'VRChat 观察',
    subtitle: 'VRChat · 世界停留', color: '#8870ba',
    fields: ['worldId', 'instanceId'].flatMap(key => text(p[key]) ? [{ label: key, value: text(p[key]) }] : []),
  }),
}

export function hasFactView(source: string): boolean { return Object.prototype.hasOwnProperty.call(views, source) }
export function presentFact(fact: ExperienceSegment): FactPresentation {
  return hasFactView(fact.source) ? views[fact.source]!(fact, object(fact.payload))
    : { title: '原始观察', subtitle: fact.source, color: '#8793a3', fields: [] }
}
export function rangeOf(fact: ExperienceSegment): TimeRange {
  return { start: Date.parse(fact.startTime), end: Date.parse(fact.endTime) }
}
export function overlaps(a: TimeRange, b: TimeRange): boolean {
  return a.start === a.end ? a.start >= b.start && a.start < b.end
    : b.start === b.end ? b.start >= a.start && b.start < a.end
    : a.start < b.end && a.end > b.start
}
export function relatedBrowser(fact: ExperienceSegment, facts: ExperienceSegment[]): ExperienceSegment[] {
  if (fact.source !== 'system' || fact.appIdentityId == null) return []
  return facts.filter(other => other.source === 'browser' && other.subjectId === fact.subjectId &&
    other.appIdentityId === fact.appIdentityId && overlaps(rangeOf(fact), rangeOf(other)))
}
export function clampRange(range: TimeRange, bounds: TimeRange): TimeRange {
  const span = Math.min(bounds.end - bounds.start, Math.max(1000, range.end - range.start))
  const start = Math.max(bounds.start, Math.min(range.start, bounds.end - span))
  return { start, end: start + span }
}
export function zoomRange(range: TimeRange, bounds: TimeRange, factor: number, pivot = .5): TimeRange {
  const span = (range.end - range.start) * factor
  const time = range.start + (range.end - range.start) * pivot
  return clampRange({ start: time - span * pivot, end: time + span * (1 - pivot) }, bounds)
}

export function groupSubjects(facts: ExperienceSegment[], range: TimeRange) {
  const groups = new Map<string, { id: string; name: string; kind: string; facts: ExperienceSegment[] }>()
  for (const fact of facts) {
    if (!overlaps(rangeOf(fact), range)) continue
    let group = groups.get(fact.subjectId)
    if (!group) {
      group = { id: fact.subjectId, name: fact.subjectName || fact.subjectId, kind: fact.subjectKind, facts: [] }
      groups.set(fact.subjectId, group)
    }
    group.facts.push(fact)
  }
  return [...groups.values()].sort((a, b) => a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name))
}

/** Groups raw System facts into application rows within each Subject, without coalescing intervals. */
export function groupApplications(facts: ExperienceSegment[]) {
  const groups = new Map<string, { id: string; appId: number | null; name: string; facts: ExperienceSegment[] }>()
  for (const fact of facts) {
    if (fact.source !== 'system') continue
    const identity = fact.appId != null ? `app:${fact.appId}` : fact.appIdentityId != null
      ? `identity:${fact.appIdentityId}` : `stream:${fact.streamId}`
    const id = `${fact.subjectId}/${identity}`
    let group = groups.get(id)
    if (!group) {
      group = { id, appId: fact.appId, name: isAwayApp(fact.appKey, fact.appName) ? '离开' : fact.appName || '前台活动', facts: [] }
      groups.set(id, group)
    }
    group.facts.push(fact)
  }
  return [...groups.values()].sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id))
}
