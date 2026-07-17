import { Client, ApiException, DailyRecapResponse, DailyReportResponse, WeeklyReportResponse, AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, SegmentResponse, UpdateMySettingsRequest } from './client'
import { authStore } from '../stores/auth'

// ===== Error model =====
// 取数失败的归一形态。让取数策略层能区分"出错"(network/http/parse)与"没数据"(空数组)。
export type ApiError =
  | { kind: 'network' }               // fetch 抛 TypeError:断网 / DNS / CORS
  | { kind: 'http'; status: number }  // 4xx/5xx:NSwag ApiException.status
  | { kind: 'parse' }                 // 响应体不是预期结构

/** 把 NSwag ApiException、原生 fetch TypeError、以及其它意外统一成 ApiError。 */
export function toApiError(e: unknown): ApiError {
  if (ApiException.isApiException(e)) return { kind: 'http', status: e.status }
  if (e instanceof TypeError) return { kind: 'network' }
  return { kind: 'parse' }
}

// ===== Base URL =====
const BASE_URL = ''
const API_BASE = '/api/v1'

// ===== Auth-aware fetch wrapper =====
const authHttp = {
  async fetch(url: RequestInfo, init?: RequestInit): Promise<Response> {
    const token = authStore.token.value
    if (token) {
      const headers = new Headers(init?.headers)
      headers.set('Authorization', `Bearer ${token}`)
      init = { ...init, headers }
    }

    let response = await fetch(url, init)

    if (response.status === 401) {
      const refreshed = await authStore.tryRefresh()
      if (refreshed) {
        const headers = new Headers(init?.headers)
        headers.set('Authorization', `Bearer ${authStore.token.value}`)
        response = await fetch(url, { ...init, headers })
      } else {
        authStore.clearAuth()
      }
    }

    return response
  },
}

const client = new Client(BASE_URL, authHttp)

// Re-export generated types
export type { AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, DailyRecapResponse, DailyReportResponse, WeeklyReportResponse, SegmentResponse }
export type { AppDurationItem } from './client'

export interface AppSummary {
  appId: number
  appName: string
  totalSeconds: number
}

/**
 * 将 "yyyy-MM-dd" 格式化为带本地时区偏移的 ISO 字符串，如 "2026-03-06T00:00:00+08:00"。
 *
 * 报表端点(daily/weekly)必须用它手拼查询串、不能走生成的 client 方法:
 * NSwag 生成的方法把 Date 序列化为 toISOString()(UTC),会丢掉本地时区偏移,
 * 而服务端 DateRange.Day/Week 靠参数的 Offset 划定"今天/本周"边界(见 shared/CONTEXT.md)。
 * usage/segments 的 start/end 是时刻过滤,UTC 表示同一瞬间,不受影响。
 */
function toLocalDateTimeOffsetString(dateStr: string): string {
  const offset = new Date().getTimezoneOffset()
  const sign = offset <= 0 ? '+' : '-'
  const absMin = Math.abs(offset)
  const h = String(Math.floor(absMin / 60)).padStart(2, '0')
  const m = String(absMin % 60).padStart(2, '0')
  return `${dateStr}T00:00:00${sign}${h}:${m}`
}

/**
 * 报表(daily/weekly)查询串:deviceId 可选,date 带本地时区偏移(见 toLocalDateTimeOffsetString)。
 * 认证版与 public 版共用同一套拼法。
 */
function reportDateParams(params: { deviceId?: number; date?: string }): URLSearchParams {
  const searchParams = new URLSearchParams()
  if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
  if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
  return searchParams
}

/** 手拼报表请求的公共尾巴:非 2xx 归一成 ApiException(与生成 client 同型),响应体走 fromJS。 */
async function reportRequest<T>(doFetch: (url: string) => Promise<Response>, url: string, fromJS: (data: unknown) => T): Promise<T> {
  const res = await doFetch(url)
  if (!res.ok) throw new ApiException('Report request failed.', res.status, await res.text(), {}, null)
  return fromJS(await res.json())
}

/** 键频项的 null 归一:生成类型里 keys / code / count 都可空,收敛成密实数组。 */
export interface KeyFrequencyItem {
  code: number
  count: number
}
function normalizeKeyFrequency(res: { keys?: { code?: number; count?: number }[] }): KeyFrequencyItem[] {
  return (res.keys ?? []).map(k => ({ code: k.code ?? 0, count: k.count ?? 0 }))
}

/** 获取浏览器时区标签，如 "UTC+8" */
export function getTimezoneLabel(): string {
  const offset = new Date().getTimezoneOffset()
  const sign = offset <= 0 ? '+' : '-'
  const absMin = Math.abs(offset)
  const h = Math.floor(absMin / 60)
  const m = absMin % 60
  return `UTC${sign}${h}${m > 0 ? ':' + String(m).padStart(2, '0') : ''}`
}

// ===== API Functions (authenticated, own data) =====

export async function fetchDevices(): Promise<DeviceInfoResponse[]> {
  return client.getDevices()
}

export async function fetchApps(): Promise<AppInfoResponse[]> {
  return client.getApps()
}

export async function fetchDeviceStatus(deviceId: number): Promise<DeviceStatusResponse> {
  return client.getDevice(deviceId)
}

export async function fetchUsage(params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  return client.getUsage(
    params.deviceId,
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
}

// daily/weekly 报表(认证版)不走生成的 client:时区偏移必须存活,见 toLocalDateTimeOffsetString。
export async function fetchDailyReport(params: {
  deviceId?: number
  date?: string
}): Promise<DailyReportResponse> {
  return reportRequest(u => authHttp.fetch(u), `${API_BASE}/reports/daily?${reportDateParams(params)}`, DailyReportResponse.fromJS)
}

export async function fetchWeeklyReport(params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReportResponse> {
  return reportRequest(u => authHttp.fetch(u), `${API_BASE}/reports/weekly?${reportDateParams(params)}`, WeeklyReportResponse.fromJS)
}

export function getIconUrl(username: string, appId: number): string {
  return `${API_BASE}/users/${encodeURIComponent(username)}/apps/${appId}/icon`
}

// ===== Recap（ADR-023）=====
// 认证版专属：叙事是私人记忆，且生成烧 LLM token，不提供 public 版。
// date 与报表同理必须携带本地时区偏移，手拼请求（见 toLocalDateTimeOffsetString）。

export async function fetchDailyRecap(params: { date?: string; force?: boolean }): Promise<DailyRecapResponse> {
  const searchParams = new URLSearchParams()
  if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
  if (params.force) searchParams.set('force', 'true')
  const res = await authHttp.fetch(`${API_BASE}/recaps/daily?${searchParams}`)
  if (!res.ok) throw new ApiException('Recap request failed.', res.status, await res.text(), {}, null)
  return DailyRecapResponse.fromJS(await res.json())
}

/** 公开 Recap 只读取 owner 已生成的缓存，匿名访问永不触发 LLM 生成。 */
export async function fetchPublicDailyRecap(username: string, params: { date?: string }): Promise<DailyRecapResponse> {
  const searchParams = new URLSearchParams()
  if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
  const res = await authHttp.fetch(`${API_BASE}/users/${encodeURIComponent(username)}/recaps/daily?${searchParams}`)
  if (!res.ok) throw new ApiException('Public recap request failed.', res.status, await res.text(), {}, null)
  return DailyRecapResponse.fromJS(await res.json())
}

// ===== Me（本人视角,ADR-025）=====
// GET /me 是懒建供给的触发点:登录后必须调一次,否则 User 行不存在,
// 本人的 /:username 看板会 404(可见性门查不到用户)。

export interface MeSettings {
  username: string
  isPublic: boolean
}

export async function fetchMe(): Promise<MeSettings> {
  const res = await client.getMe()
  return { username: res.username ?? '', isPublic: res.isPublic ?? false }
}

export async function updateMySettings(isPublic: boolean): Promise<MeSettings> {
  const res = await client.updateMySettings(UpdateMySettingsRequest.fromJS({ isPublic }))
  return { username: res.username ?? '', isPublic: res.isPublic ?? false }
}

// ===== Public API Functions (no auth required, by username) =====
// 统一走 NSwag 生成的 client 方法(响应类型由 OpenAPI schema 保证);
// 唯二例外是 daily/weekly 报表——时区偏移必须存活,见 toLocalDateTimeOffsetString。

export async function fetchPublicDevices(username: string): Promise<DeviceInfoResponse[]> {
  return client.getUserDevices(username)
}

export async function fetchPublicApps(username: string): Promise<AppInfoResponse[]> {
  return client.getUserApps(username)
}

export async function fetchPublicDailyReport(username: string, params: {
  deviceId?: number
  date?: string
}): Promise<DailyReportResponse> {
  // authHttp:可见性门（ADR-025）下本人看 private 看板靠 JWT 识别,裸 fetch 会 404
  return reportRequest(u => authHttp.fetch(u), `${API_BASE}/users/${username}/reports/daily?${reportDateParams(params)}`, DailyReportResponse.fromJS)
}

export async function fetchPublicWeeklyReport(username: string, params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReportResponse> {
  return reportRequest(u => authHttp.fetch(u), `${API_BASE}/users/${username}/reports/weekly?${reportDateParams(params)}`, WeeklyReportResponse.fromJS)
}

export async function fetchPublicDeviceStatus(username: string, deviceId: number): Promise<DeviceStatusResponse> {
  return client.getUserDeviceStatus(username, deviceId)
}

export async function fetchPublicUsage(username: string, params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  return client.getUserUsage(
    username,
    params.deviceId,
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
}

export async function fetchPublicSegments(username: string, params: {
  deviceId?: number
  source?: string
  appId?: number
  start?: string
  end?: string
}): Promise<SegmentResponse[]> {
  return client.getUserSegments(
    username,
    params.deviceId,
    params.source,
    params.appId,
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
}

export async function fetchPublicKeyFrequency(username: string, params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<KeyFrequencyItem[]> {
  const res = await client.getUserKeyFrequency(
    username,
    params.deviceId,
    params.start ? new Date(params.start) : undefined,
    params.end ? new Date(params.end) : undefined,
  )
  return normalizeKeyFrequency(res)
}
