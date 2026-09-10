import { authHttp } from './index'

import type { IPersonAssociationResponse, IPersonFactPage, IPersonSettingsResponse } from './client'

// Generated DTOs remain authoritative. This transport keeps JSON ISO text because NSwag's
// Date conversion discards microseconds. Exclude class methods and extensibility indexers.
type JsonWire<T> = T extends Date ? string
  : T extends undefined ? null
  : T extends Array<infer Item> ? JsonWire<Item>[]
  : T extends object ? {
    [Key in keyof T as string extends Key ? never : number extends Key ? never
      : T[Key] extends (...args: never[]) => unknown ? never : Key]: JsonWire<T[Key]>
  } : T
export type PersonAssociation = JsonWire<IPersonAssociationResponse>
export type PersonSettings = JsonWire<IPersonSettingsResponse>
export type PersonFactPage = JsonWire<IPersonFactPage>
async function request(path: string, method = 'GET', body?: unknown): Promise<Response> {
  const response = await authHttp.fetch('/api/v1/me/person' + path, {
    method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) throw new Error(`请求失败（${response.status}），请检查范围或刷新后重试。`)
  return response
}
export async function fetchPersonSettings(): Promise<PersonSettings> { return (await request('')).json() }
export async function establishPerson(): Promise<void> { await request('', 'PUT') }
export async function savePersonAssociation(id: number | null, value: Omit<PersonAssociation, 'id'>): Promise<void> {
  await request('/associations' + (id === null ? '' : '/' + id), id === null ? 'POST' : 'PUT', value)
}
export async function removePersonAssociation(id: number): Promise<void> { await request('/associations/' + id, 'DELETE') }
export async function fetchPersonFacts(family: string, start: string | null, end: string | null, offset: number): Promise<PersonFactPage> {
  const params = new URLSearchParams({ offset: String(offset), limit: '20' })
  if (start) params.set('start', start)
  if (end) params.set('end', end)
  return (await request(`/facts/${family}?${params}`)).json()
}
