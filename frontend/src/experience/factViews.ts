import { relatedObject } from '../observationRelations'
export { relatedObject } from '../observationRelations'
import type { IObjectSummary, IRelationResponse } from '../api/client'
import type { JsonWire } from '../api/wire'
import { isAwayApp } from '../appLabels'

export interface ExperienceSegment {
  id: string
  streamId?: string | null
  factId?: string | null
  revision: number
  collectorId?: string | null
  foi?: JsonWire<IObjectSummary> | null
  relations?: JsonWire<IRelationResponse>[]
  deviceId?: number | null
  source: string | null
  aspect?: string | null
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
  'desktop-activity': (fact, p) => isAwayApp(fact.appKey, fact.appName) ? {
    title: '离开', subtitle: '离开区间', color: '#8893a1', fields: [],
  } : ({
    title: text(p.title) || fact.appName || '前台活动',
    subtitle: fact.appName || '前台活动', color: '#388bb5', fields: [],
  }),
  'selected-page': (fact, p) => {
    const attrs = object(p.attributes)
    const url = text(attrs.url)
    return {
      title: text(p.title) || text(attrs.site) || '浏览器页面',
      subtitle: text(attrs.site) || text(attrs.domain) || fact.appName || 'Browser',
      color: '#bd8843',
      fields: url ? [{ label: 'URL', value: url, href: safeUrl(url) }] : [],
    }
  },
  'account-location': (fact, p) => ({
    title: text(p.worldName) || text(p.title) || text(p.worldId) || '账号位置',
    subtitle: fact.appName ? `${fact.appName} · 账号位置` : '账号位置', color: '#8870ba',
    fields: ['worldId', 'instanceId'].flatMap(key => text(p[key]) ? [{ label: key, value: text(p[key]) }] : []),
  }),
}

export function hasFactView(aspect: string | null | undefined): boolean { return aspect != null && Object.prototype.hasOwnProperty.call(views, aspect) }
export function presentFact(fact: ExperienceSegment): FactPresentation {
  return hasFactView(fact.aspect) ? views[fact.aspect!]!(fact, object(fact.payload))
    : { title: '原始观察', subtitle: fact.source ?? '未知来源', color: '#8793a3', fields: [] }
}
export function rangeOf(fact: ExperienceSegment): TimeRange {
  return { start: Date.parse(fact.startTime), end: Date.parse(fact.endTime) }
}
export function overlaps(a: TimeRange, b: TimeRange): boolean {
  return a.start === a.end ? a.start >= b.start && a.start < b.end
    : b.start === b.end ? b.start >= a.start && b.start < a.end
    : a.start < b.end && a.end > b.start
}
export function relatedPages(fact: ExperienceSegment, facts: ExperienceSegment[]): ExperienceSegment[] {
  if (fact.aspect !== 'desktop-activity') return []
  const machine = relatedObject(fact, 'device'), app = relatedObject(fact, 'app')
  if (!machine || !app) return []
  return facts.filter(other => other.aspect === 'selected-page' && overlaps(rangeOf(fact), rangeOf(other)) &&
    relatedObject(other, 'device')?.id === machine.id && relatedObject(other, 'app')?.id === app.id)
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

/** A stream can distinguish unknown history in the view; it never becomes an Object. */
export function factObject(fact: Pick<ExperienceSegment, 'foi' | 'streamId'>) {
  return fact.foi ? { id: fact.foi.id, kind: fact.foi.kind, name: fact.foi.name || fact.foi.key }
    : { id: `unknown:${fact.streamId}`, kind: 'unknown', name: '未知对象' }
}

export function groupObjects(facts: ExperienceSegment[], range: TimeRange) {
  const groups = new Map<string, { id: string; name: string; kind: string; facts: ExperienceSegment[] }>()
  for (const fact of facts) {
    if (!overlaps(rangeOf(fact), range)) continue
    const object = factObject(fact)
    let group = groups.get(object.id)
    if (!group) {
      group = { ...object, facts: [] }
      groups.set(object.id, group)
    }
    group.facts.push(fact)
  }
  return [...groups.values()].sort((a, b) => a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name))
}

/** Group foreground facts by their confirmed App object; preserve each original interval. */
export function groupApplications(facts: ExperienceSegment[]) {
  const groups = new Map<string, { id: string; appId: number | null; name: string; facts: ExperienceSegment[] }>()
  for (const fact of facts) {
    if (fact.aspect !== 'desktop-activity') continue
    const app = relatedObject(fact, 'app')
    const identity = app?.id ?? `unknown:${fact.streamId}`
    const id = `${factObject(fact).id}/${identity}`
    let group = groups.get(id)
    if (!group) {
      group = { id, appId: fact.appId, name: isAwayApp(fact.appKey, fact.appName) ? '离开' : app?.name || app?.key || '前台活动', facts: [] }
      groups.set(id, group)
    }
    group.facts.push(fact)
  }
  return [...groups.values()].sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id))
}
