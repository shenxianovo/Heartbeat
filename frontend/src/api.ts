import type { AppUsage, DeviceInfo, DeviceStatus } from './types'

const BASE = import.meta.env.DEV
  ? '/api/v1'
  : '/heartbeat/api/v1'

export async function fetchDevices(): Promise<DeviceInfo[]> {
  const res = await fetch(`${BASE}/devices`)
  if (!res.ok) return []
  return res.json()
}

export async function fetchDeviceStatus(deviceId: number): Promise<DeviceStatus | null> {
  const res = await fetch(`${BASE}/devices/${deviceId}/status`)
  if (!res.ok) return null
  return res.json()
}

export async function fetchUsage(deviceId: number, date: string): Promise<AppUsage[]> {
  const params = new URLSearchParams({ date })
  const res = await fetch(`${BASE}/devices/${deviceId}/usage?${params}`)
  if (!res.ok) return []
  return res.json()
}

export function getIconUrl(appId: number): string {
  return `${BASE}/apps/${appId}/icon`
}
