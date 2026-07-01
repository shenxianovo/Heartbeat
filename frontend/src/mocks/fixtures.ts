// 公开面板的 mock 数据（单一正常 scene）。
// 形状严格对齐 src/api/client.ts 里 NSwag 生成的类型：
//   - startTime/endTime/lastSeen: ISO 字符串（会被 new Date() 解析）
//   - date/weekStart/weekEnd: 纯日期字符串
//   - isOnline: boolean

export interface MockApp {
  id: number
  name: string
}

export interface MockDevice {
  id: number
  name: string
}

export const apps: MockApp[] = [
  { id: 1, name: 'Visual Studio Code' },
  { id: 2, name: 'Google Chrome' },
  { id: 3, name: 'Spotify' },
  { id: 4, name: 'Slack' },
  { id: 5, name: 'Terminal' },
  { id: 6, name: 'Figma' },
  { id: 99, name: '__away__' },
]

export const devices: MockDevice[] = [
  { id: 1, name: 'MacBook Pro' },
  { id: 2, name: 'Desktop-Win' },
]

/** 当前正在使用的 app（用于 deviceStatus.currentApp） */
export const CURRENT_APP_NAME = 'Visual Studio Code'

/** 一天内的 app 时长分布（秒）。用于日报 + ranking。 */
export const dailyAppDurations: { appId: number; appName: string; durationSeconds: number }[] = [
  { appId: 1, appName: 'Visual Studio Code', durationSeconds: 18420 }, // 5h7m
  { appId: 2, appName: 'Google Chrome', durationSeconds: 9360 },        // 2h36m
  { appId: 99, appName: '__away__', durationSeconds: 5400 },            // 1h30m 离开
  { appId: 4, appName: 'Slack', durationSeconds: 3600 },                // 1h
  { appId: 3, appName: 'Spotify', durationSeconds: 2700 },              // 45m
  { appId: 5, appName: 'Terminal', durationSeconds: 1500 },             // 25m
  { appId: 6, appName: 'Figma', durationSeconds: 600 },                 // 10m
]

/** 一周时长分布（秒），数值约为日报的 5-6 倍。 */
export const weeklyAppDurations: { appId: number; appName: string; durationSeconds: number }[] = [
  { appId: 1, appName: 'Visual Studio Code', durationSeconds: 95400 },
  { appId: 2, appName: 'Google Chrome', durationSeconds: 52200 },
  { appId: 99, appName: '__away__', durationSeconds: 28800 },
  { appId: 4, appName: 'Slack', durationSeconds: 19800 },
  { appId: 3, appName: 'Spotify', durationSeconds: 16200 },
  { appId: 5, appName: 'Terminal', durationSeconds: 8400 },
  { appId: 6, appName: 'Figma', durationSeconds: 3600 },
]

/**
 * 生成"今天"的 usage 时间段（timeline 用）。
 * 基于当前日期，从早上 9 点开始铺一串连续的使用段，覆盖到接近 now。
 * 返回的时间是 ISO 字符串，匹配 AppUsageResponse.startTime/endTime。
 */
export function buildTodayUsage(): {
  id: number
  appId: number
  appName: string
  title: string | null
  startTime: string
  endTime: string
  durationSeconds: number
}[] {
  const now = new Date()
  const dayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 9, 0, 0)

  // 一串 (appId, 分钟数, 标题) 的会话序列，依次首尾相接。
  const sessions: { appId: number; minutes: number; title: string | null }[] = [
    { appId: 1, minutes: 50, title: 'useHeartbeat.ts - heartbeat' },
    { appId: 2, minutes: 25, title: 'YouTube' },
    { appId: 1, minutes: 40, title: 'ActivityTimeline.vue - heartbeat' },
    { appId: 4, minutes: 15, title: '#general' },
    { appId: 99, minutes: 55, title: null },
    { appId: 3, minutes: 30, title: 'Lo-fi beats' },
    { appId: 1, minutes: 60, title: 'UsageService.cs - heartbeat' },
    { appId: 5, minutes: 20, title: 'pwsh' },
    { appId: 2, minutes: 35, title: 'GitHub - heartbeat' },
    { appId: 6, minutes: 10, title: 'Dashboard mockup' },
    { appId: 1, minutes: 45, title: 'useReports.ts - heartbeat' },
  ]

  const appNameById = new Map(apps.map((a) => [a.id, a.name]))
  const result: ReturnType<typeof buildTodayUsage> = []
  let cursor = dayStart.getTime()
  let id = 1

  for (const s of sessions) {
    const start = new Date(cursor)
    const end = new Date(cursor + s.minutes * 60_000)
    // 不要越过 now
    if (start.getTime() >= now.getTime()) break
    const clampedEnd = end.getTime() > now.getTime() ? now : end
    const durationSeconds = Math.round((clampedEnd.getTime() - start.getTime()) / 1000)
    result.push({
      id: id++,
      appId: s.appId,
      appName: appNameById.get(s.appId) ?? `App ${s.appId}`,
      title: s.title,
      startTime: start.toISOString(),
      endTime: clampedEnd.toISOString(),
      durationSeconds,
    })
    cursor = end.getTime()
  }

  return result
}

/** "yyyy-MM-dd" 本地日期字符串 */
export function todayDateStr(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** 本周一 / 本周日的日期字符串 */
export function weekRange(): { weekStart: string; weekEnd: string } {
  const d = new Date()
  const day = d.getDay() === 0 ? 7 : d.getDay() // 周日记为 7
  const monday = new Date(d.getFullYear(), d.getMonth(), d.getDate() - (day - 1))
  const sunday = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + 6)
  const fmt = (x: Date) =>
    `${x.getFullYear()}-${String(x.getMonth() + 1).padStart(2, '0')}-${String(x.getDate()).padStart(2, '0')}`
  return { weekStart: fmt(monday), weekEnd: fmt(sunday) }
}

/**
 * 键盘逐键按下次数（VK 码）。模拟常见打字分布：
 * 字母高频、空格/回车/退格次高、修饰键中等、符号偏低。
 */
export const keyFrequency: { code: number; count: number }[] = [
  { code: 32, count: 4820 },  // Space
  { code: 69, count: 3120 },  // E
  { code: 84, count: 2380 },  // T
  { code: 65, count: 2210 },  // A
  { code: 79, count: 2050 },  // O
  { code: 73, count: 1980 },  // I
  { code: 78, count: 1870 },  // N
  { code: 83, count: 1790 },  // S
  { code: 8, count: 1650 },   // Backspace
  { code: 82, count: 1540 },  // R
  { code: 72, count: 1420 },  // H
  { code: 76, count: 1310 },  // L
  { code: 68, count: 1280 },  // D
  { code: 13, count: 1180 },  // Enter
  { code: 67, count: 1090 },  // C
  { code: 85, count: 980 },   // U
  { code: 77, count: 920 },   // M
  { code: 70, count: 880 },   // F
  { code: 80, count: 810 },   // P
  { code: 71, count: 760 },   // G
  { code: 87, count: 740 },   // W
  { code: 89, count: 690 },   // Y
  { code: 66, count: 640 },   // B
  { code: 160, count: 1240 }, // LShift
  { code: 162, count: 580 },  // LCtrl
  { code: 86, count: 520 },   // V
  { code: 75, count: 480 },   // K
  { code: 9, count: 360 },    // Tab
  { code: 88, count: 320 },   // X
  { code: 74, count: 300 },   // J
  { code: 81, count: 240 },   // Q
  { code: 90, count: 180 },   // Z
  { code: 190, count: 420 },  // .
  { code: 188, count: 380 },  // ,
  { code: 191, count: 160 },  // /
  { code: 186, count: 140 },  // ;
  { code: 164, count: 220 },  // LAlt
  { code: 20, count: 90 },    // Caps
  { code: 161, count: 210 },  // RShift
  { code: 163, count: 110 },  // RCtrl
  { code: 165, count: 70 },   // RAlt
  { code: 49, count: 280 },   // 1
  { code: 50, count: 260 },   // 2
  { code: 51, count: 240 },   // 3
]
