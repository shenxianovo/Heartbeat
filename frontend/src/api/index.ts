import { Client, DailyReportResponse, WeeklyReportResponse, AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, SegmentResponse } from './client'
import { authStore } from '../stores/auth'

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
export type { AppInfoResponse, DeviceInfoResponse, DeviceStatusResponse, AppUsageResponse, DailyReportResponse, WeeklyReportResponse, SegmentResponse }
export type { AppDurationItem } from './client'

export interface AppSummary {
  appId: number
  appName: string
  totalSeconds: number
}

export interface KeyFrequencyItem {
  code: number
  count: number
}

export interface KeyFrequencyResponse {
  keys: KeyFrequencyItem[]
}

/** 将 "yyyy-MM-dd" 格式化为带本地时区偏移的 ISO 字符串，如 "2026-03-06T00:00:00+08:00" */
function toLocalDateTimeOffsetString(dateStr: string): string {
  const offset = new Date().getTimezoneOffset()
  const sign = offset <= 0 ? '+' : '-'
  const absMin = Math.abs(offset)
  const h = String(Math.floor(absMin / 60)).padStart(2, '0')
  const m = String(absMin % 60).padStart(2, '0')
  return `${dateStr}T00:00:00${sign}${h}:${m}`
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
  try {
    return await client.getDevices()
  } catch {
    return []
  }
}

export async function fetchApps(): Promise<AppInfoResponse[]> {
  try {
    return await client.getApps()
  } catch {
    return []
  }
}

export async function fetchDeviceStatus(deviceId: number): Promise<DeviceStatusResponse | null> {
  try {
    return await client.getDevice(deviceId)
  } catch {
    return null
  }
}

export async function fetchUsage(params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  try {
    return await client.getUsage(
      params.deviceId,
      params.start ? new Date(params.start) : undefined,
      params.end ? new Date(params.end) : undefined,
    )
  } catch {
    return []
  }
}

export async function fetchDailyReport(params: {
  deviceId?: number
  date?: string
}): Promise<DailyReportResponse | null> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
    const res = await authHttp.fetch(`${API_BASE}/reports/daily?${searchParams}`)
    if (!res.ok) return null
    return DailyReportResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export async function fetchWeeklyReport(params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReportResponse | null> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
    const res = await authHttp.fetch(`${API_BASE}/reports/weekly?${searchParams}`)
    if (!res.ok) return null
    return WeeklyReportResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export function getIconUrl(appId: number): string {
  return `${API_BASE}/apps/${appId}/icon`
}

// ===== Public API Functions (no auth required, by username) =====

export async function fetchPublicDevices(username: string): Promise<DeviceInfoResponse[]> {
  try {
    const res = await fetch(`${API_BASE}/users/${username}/devices`)
    if (!res.ok) return []
    return (await res.json()).map((d: any) => DeviceInfoResponse.fromJS(d))
  } catch {
    return []
  }
}

export async function fetchPublicApps(username: string): Promise<AppInfoResponse[]> {
  try {
    const res = await fetch(`${API_BASE}/users/${username}/apps`)
    if (!res.ok) return []
    return (await res.json()).map((a: any) => AppInfoResponse.fromJS(a))
  } catch {
    return []
  }
}

export async function fetchPublicDailyReport(username: string, params: {
  deviceId?: number
  date?: string
}): Promise<DailyReportResponse | null> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
    const res = await fetch(`${API_BASE}/users/${username}/reports/daily?${searchParams}`)
    if (!res.ok) return null
    return DailyReportResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export async function fetchPublicWeeklyReport(username: string, params: {
  deviceId?: number
  date?: string
}): Promise<WeeklyReportResponse | null> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.date) searchParams.set('date', toLocalDateTimeOffsetString(params.date))
    const res = await fetch(`${API_BASE}/users/${username}/reports/weekly?${searchParams}`)
    if (!res.ok) return null
    return WeeklyReportResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export async function fetchPublicDeviceStatus(username: string, deviceId: number): Promise<DeviceStatusResponse | null> {
  try {
    const res = await fetch(`${API_BASE}/users/${username}/devices/${deviceId}/status`)
    if (!res.ok) return null
    return DeviceStatusResponse.fromJS(await res.json())
  } catch {
    return null
  }
}

export async function fetchPublicUsage(username: string, params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<AppUsageResponse[]> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.start) searchParams.set('start', params.start)
    if (params.end) searchParams.set('end', params.end)
    const res = await fetch(`${API_BASE}/users/${username}/usage?${searchParams}`)
    if (!res.ok) return []
    return (await res.json()).map((u: any) => AppUsageResponse.fromJS(u))
  } catch {
    return []
  }
}

export async function fetchPublicSegments(username: string, params: {
  deviceId?: number
  source?: string
  appId?: number
  start?: string
  end?: string
}): Promise<SegmentResponse[]> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.source) searchParams.set('source', params.source)
    if (params.appId !== undefined) searchParams.set('appId', String(params.appId))
    if (params.start) searchParams.set('start', params.start)
    if (params.end) searchParams.set('end', params.end)
    const res = await fetch(`${API_BASE}/users/${username}/segments?${searchParams}`)
    if (!res.ok) return []
    return (await res.json()).map((s: any) => SegmentResponse.fromJS(s))
  } catch {
    return []
  }
}

export async function fetchPublicKeyFrequency(username: string, params: {
  deviceId?: number
  start?: string
  end?: string
}): Promise<KeyFrequencyResponse> {
  try {
    const searchParams = new URLSearchParams()
    if (params.deviceId !== undefined) searchParams.set('deviceId', String(params.deviceId))
    if (params.start) searchParams.set('start', params.start)
    if (params.end) searchParams.set('end', params.end)
    const res = await fetch(`${API_BASE}/users/${username}/input-events/key-frequency?${searchParams}`)
    if (!res.ok) return { keys: [] }
    return await res.json()
  } catch {
    return { keys: [] }
  }
}
