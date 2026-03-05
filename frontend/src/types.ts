export interface DeviceInfo {
  id: number
  name: string
}

export interface AppUsage {
  id: number
  appId: number
  appName: string
  startTime: string
  endTime: string
  durationSeconds: number
}

export interface AppSummary {
  appId: number
  appName: string
  totalSeconds: number
}

export interface DeviceStatus {
  currentApp: string | null
  lastSeen: string | null
  isOnline: boolean
}
