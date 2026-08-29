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

const RECAP_NARRATIVE =
  '上午你大部分时间在 vscode 里，围绕 Heartbeat 项目的服务端代码来回打磨，中途穿插着几段浏览器查阅——EF Core 迁移文档和几篇 Stack Overflow 讨论。\n\n下午的节奏慢了下来，你在 chrome 里看了将近一小时的技术视频，随后回到编辑器继续收尾。傍晚有一段四十分钟的离开，回来后你只做了些零碎的整理便合上了电脑。'

/**
 * Recap 读取与流式生成的 mock 开关（ADR-042）。GET 是纯读——三态与两个判脏位由这里摆出来；
 * 生成走 SSE，分块、中途失败、并发 409 都要能手工拨到，否则流式的失败路径在 mock 里永远看不见。
 * 改这些字段即时生效（dev:mock 下在 console 里改也行）。
 */
export const recapMock = {
  /** GET 的三态：'narrative' 有叙事 / 'notGenerated' 有数据但从未生成 / 'empty' 空日。 */
  state: 'narrative' as 'narrative' | 'notGenerated' | 'empty',
  segmentStale: false,
  knowledgeStale: false,
  /**
   * 正文之前的推理增量（ADR-042 §9）。真实模型在这里能沉默上百秒、吐一万多字符，
   * mock 里给足够多的块，才能看出推理面板到底会不会溢出、会不会自动滚底。
   */
  thinkingChunks: [
    '先看这一天的 digest：段数据从 09:12 开始，',
    'vscode 占了上午的大头，中间几次切到 chrome。\n',
    '需要判断这几次切换是查资料还是分心——',
    '看标题里有 EF Core 迁移和 Stack Overflow，倾向于查资料。\n',
    '下午 14:30 起有一段近一小时的 chrome 视频播放，',
    '与上午的编码不同源，单独成段更贴近真实节奏。\n',
    '傍晚 18:10 到 18:50 没有任何段：离开，不是空闲。\n',
    '结构定了：上午打磨代码（含查阅）、下午慢下来、傍晚离开后收尾。开始写。',
  ],
  /** 每块推理之间的间隔，肉眼观察滚动节奏用。 */
  thinkingDelayMs: 400,
  /** 生成时吐出的分块；其中一块以空行开头，用来看段落是对累积文本重算的。 */
  chunks: ['上午你在 vscode 里', '打磨服务端代码，', '\n\n下午读了一会儿文档，', '傍晚离开了四十分钟。'],
  /** 每块之间的间隔，肉眼观察流式节奏用。 */
  delayMs: 120,
  /** 吐完第 n 块后发 error 事件（null = 不失败）。0 表示首块之前就失败。 */
  failAfterChunks: null as number | null,
  /** 生成失败的可读原因。 */
  errorMessage: '生成服务暂时不可用，请稍后重试',
  /** 并发撞锁：不是 SSE，而是 409 + 一句可读原因（ADR-042 §7）。 */
  conflict: false,
}

function recapResponse(narrative: string | null) {
  return {
    date: todayDateStr(),
    isEmpty: recapMock.state === 'empty',
    narrative,
    generatedAt: narrative ? new Date().toISOString() : null,
    model: narrative ? 'mock-model' : null,
    knowledgeStale: recapMock.knowledgeStale,
    segmentStale: recapMock.segmentStale,
  }
}

/** 一帧 SSE：`event:` + `data:` + 空行。心跳是 `event: ping`（.NET 输出不了注释行）。 */
function sseFrame(event: string, data: unknown): string {
  return `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`
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

  // GET /users/:username/reports/daily?deviceId&version&kind&localDate&timeZone&start&endExclusive
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

  // GET /recaps/daily?date（认证版；mock 环境无鉴权）。纯读：永不生成、永不写"库"。
  http.get(`${API}/recaps/daily`, () => {
    // 三态里"空日"和"从未生成"都没有叙事，靠 isEmpty 区分（ADR-042 §3）
    return HttpResponse.json(recapResponse(recapMock.state === 'narrative' ? RECAP_NARRATIVE : null))
  }),

  // POST /recaps/daily/generate?date —— SSE 流式生成（ADR-042 §4）
  // 生成域的失败不走状态码，走流内 error；只有并发撞锁这种"流还没开始"的失败才是 409。
  http.post(`${API}/recaps/daily/generate`, () => {
    if (recapMock.conflict) {
      // 与服务端一致：TypedResults.Conflict(string) 输出的是带引号的 JSON 字符串
      return HttpResponse.json('这一天正在生成中。', { status: 409 })
    }

    const encoder = new TextEncoder()
    const stream = new ReadableStream({
      async start(controller) {
        const emit = (event: string, data: unknown) => controller.enqueue(encoder.encode(sseFrame(event, data)))
        // 心跳连接建立即开始，前端必须忽略它
        emit('ping', {})
        // 推理先于正文：思考期是真实模型最长的一段，前端要在这段时间里滚动显示它
        for (const thinking of recapMock.thinkingChunks) {
          if (recapMock.thinkingDelayMs > 0) await new Promise(r => setTimeout(r, recapMock.thinkingDelayMs))
          emit('thinking', { thinking })
        }
        let accumulated = ''
        if (recapMock.failAfterChunks === 0) {
          emit('error', { message: recapMock.errorMessage })
          controller.close()
          return
        }
        for (const [i, chunk] of recapMock.chunks.entries()) {
          if (recapMock.delayMs > 0) await new Promise(r => setTimeout(r, recapMock.delayMs))
          accumulated += chunk
          emit('delta', { delta: chunk })
          if (recapMock.failAfterChunks !== null && i + 1 >= recapMock.failAfterChunks) {
            // 中途失败：不落"库"，前端应退回上次成功的叙事
            emit('error', { message: recapMock.errorMessage })
            controller.close()
            return
          }
        }
        emit('done', { recap: recapResponse(accumulated) })
        controller.close()
      },
    })

    return new HttpResponse(stream, {
      headers: { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' },
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
