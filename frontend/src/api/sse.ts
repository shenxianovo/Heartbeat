/**
 * SSE 帧解析（ADR-042 §4）：流式生成端点不进 codegen，前端用 fetch + ReadableStream 手工读流——
 * `EventSource` 只能 GET 且带不了 `Authorization` 头，而认证是 Bearer。
 *
 * 这一层只做"字节 → 帧"，与 fetch、认证、业务事件解耦：字节流的三种真实切断方式（多字节字符被
 * chunk 切断、分帧空行落在两块之间、注释行）在这里是纯函数，可以直接测，不必架一条假的 HTTP。
 */

/** 一个完整的 SSE 帧。event 缺省按规范是 `message`；多行 data 按规范用 `\n` 拼接。 */
export interface SseFrame {
  event: string
  data: string
}

export interface SseFrameParser {
  /** 喂一块字节，返回本块凑齐的完整帧（可能 0 个，也可能多个）。 */
  push(chunk: Uint8Array): SseFrame[]
  /** 流结束：冲掉解码器里残留的多字节尾巴。未以空行收尾的残帧按规范丢弃。 */
  flush(): SseFrame[]
}

/** 创建一个增量解析器。有状态（跨 chunk 的半行与半个多字节字符），一条流一个实例。 */
export function createSseFrameParser(): SseFrameParser {
  // stream: true 让被 chunk 切断的多字节字符留在解码器里，等下一块补齐后再吐出完整字符
  const decoder = new TextDecoder('utf-8')
  let buffer = ''
  let event: string | null = null
  let dataLines: string[] = []

  /** 空行 = 帧结束。一个字段都没收到的块（例如纯注释）不成帧。 */
  function endFrame(): SseFrame | null {
    if (event === null && dataLines.length === 0) return null
    const frame: SseFrame = { event: event ?? 'message', data: dataLines.join('\n') }
    event = null
    dataLines = []
    return frame
  }

  function feedLine(line: string): SseFrame | null {
    if (line === '') return endFrame()
    if (line.startsWith(':')) return null // 注释行（有些服务端拿它做心跳）
    const colon = line.indexOf(':')
    const field = colon === -1 ? line : line.slice(0, colon)
    let value = colon === -1 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1) // 规范只吃掉一个前导空格
    if (field === 'event') event = value
    else if (field === 'data') dataLines.push(value)
    // id / retry 与未知字段一律忽略：这条流不做断线重连（生成一旦开始就不重试）
    return null
  }

  /** 把 buffer 里已经确定结束的行喂进去；半行留着等下一块。 */
  function drain(): SseFrame[] {
    const frames: SseFrame[] = []
    for (;;) {
      const m = /\r\n|\n|\r/.exec(buffer)
      if (!m) break
      // 块尾那个孤零零的 \r 可能是被切断的 \r\n，留到下一块再判，否则会凭空多切一个空行出来
      if (m[0] === '\r' && m.index === buffer.length - 1) break
      const line = buffer.slice(0, m.index)
      buffer = buffer.slice(m.index + m[0].length)
      const frame = feedLine(line)
      if (frame) frames.push(frame)
    }
    return frames
  }

  return {
    push(chunk: Uint8Array): SseFrame[] {
      buffer += decoder.decode(chunk, { stream: true })
      return drain()
    },
    flush(): SseFrame[] {
      buffer += decoder.decode()
      const frames = drain()
      // 没有空行收尾的残帧按规范丢弃：半个 JSON 解不出东西，宁可少一帧也不要错一帧
      buffer = ''
      event = null
      dataLines = []
      return frames
    },
  }
}
