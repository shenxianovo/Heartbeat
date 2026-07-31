import { http, HttpResponse } from 'msw'
import {
  apps,
  devices,
  CURRENT_APP_NAME,
  dailyAppDurations,
  weeklyAppDurations,
  buildTodayUsage,
  todayDateStr,
  weekRange,
  keyFrequency,
} from './fixtures'

// 用 `*` 通配前缀，匹配任意 origin 下的 /api/v1 路径，
// 浏览器（同源 fetch）和 node 验证环境都能命中。
const API = '*/api/v1'

/** 按 appId 生成一个确定性的色块 SVG（不同 hue + 首字母）。 */
function iconSvg(appId: number): string {
  const hue = (appId * 47) % 360
  const bg = `hsl(${hue}, 65%, 55%)`
  const app = apps.find((a) => a.id === appId)
  const letter = (app?.name ?? '?').charAt(0).toUpperCase()
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="12" fill="${bg}"/>
  <text x="32" y="42" font-family="sans-serif" font-size="32" font-weight="600"
    fill="white" text-anchor="middle">${letter}</text>
</svg>`
}

export const handlers = [
  // GET /users/:username/devices
  http.get(`${API}/users/:username/devices`, () => {
    return HttpResponse.json(devices)
  }),

  // GET /users/:username/apps
  http.get(`${API}/users/:username/apps`, () => {
    return HttpResponse.json(apps)
  }),

  // GET /users/:username/devices/:deviceId/status
  // 设备 1 在线且有前台应用；设备 2 在线但人离开（__away__）——presence 芯片的两种形态。
  http.get(`${API}/users/:username/devices/:deviceId/status`, ({ params }) => {
    const deviceId = Number(params.deviceId)
    if (deviceId === 2) {
      return HttpResponse.json({
        id: 2,
        currentApp: '__away__',
        lastSeen: new Date(Date.now() - 8000).toISOString(),
        isOnline: true,
      })
    }
    return HttpResponse.json({
      id: deviceId,
      currentApp: CURRENT_APP_NAME,
      lastSeen: new Date(Date.now() - 3000).toISOString(), // 贴近 now，显得"刚刚活着"
      isOnline: true,
    })
  }),

  // GET /users/:username/usage?deviceId&start&end
  // deviceId 缺省 = 聚合（全部设备）；带 deviceId = 只返回该设备的段。
  http.get(`${API}/users/:username/usage`, ({ request }) => {
    const deviceId = new URL(request.url).searchParams.get('deviceId')
    const all = buildTodayUsage()
    return HttpResponse.json(
      deviceId ? all.filter((u) => u.deviceId === Number(deviceId)) : all,
    )
  }),

  // GET /users/:username/reports/daily?deviceId&date
  // 聚合时返回两台设备的时长求和,单设备时按比例缩减,便于肉眼校验主/副数字。
  http.get(`${API}/users/:username/reports/daily`, ({ request }) => {
    const deviceId = new URL(request.url).searchParams.get('deviceId')
    const apps = deviceId
      ? dailyAppDurations.map((a) => ({
          ...a,
          durationSeconds: Math.round(a.durationSeconds * (Number(deviceId) === 1 ? 0.65 : 0.35)),
        }))
      : dailyAppDurations
    return HttpResponse.json({ date: todayDateStr(), apps })
  }),

  // GET /users/:username/reports/weekly?deviceId&date
  http.get(`${API}/users/:username/reports/weekly`, () => {
    const { weekStart, weekEnd } = weekRange()
    return HttpResponse.json({
      weekStart,
      weekEnd,
      apps: weeklyAppDurations,
    })
  }),

  // GET /users/:username/input-events/key-frequency?deviceId&start&end
  http.get(`${API}/users/:username/input-events/key-frequency`, () => {
    return HttpResponse.json({ keys: keyFrequency })
  }),

  // GET /recaps/daily?date&force（认证版；mock 环境无鉴权，直接返回叙事）
  http.get(`${API}/recaps/daily`, () => {
    return HttpResponse.json({
      date: todayDateStr(),
      isEmpty: false,
      narrative:
        '上午你大部分时间在 vscode 里，围绕 Heartbeat 项目的服务端代码来回打磨，中途穿插着几段浏览器查阅——EF Core 迁移文档和几篇 Stack Overflow 讨论。\n\n下午的节奏慢了下来，你在 chrome 里看了将近一小时的技术视频，随后回到编辑器继续收尾。傍晚有一段四十分钟的离开，回来后你只做了些零碎的整理便合上了电脑。',
      generatedAt: new Date().toISOString(),
      model: 'mock-model',
    })
  }),

  // GET /users/:username/apps/:appId/icon —— 返回按 appId 生成的色块 SVG
  http.get(`${API}/users/:username/apps/:appId/icon`, ({ params }) => {
    const appId = Number(params.appId)
    return new HttpResponse(iconSvg(appId), {
      headers: { 'Content-Type': 'image/svg+xml' },
    })
  }),
]
