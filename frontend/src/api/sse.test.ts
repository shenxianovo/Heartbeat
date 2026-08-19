import { describe, expect, it } from 'vitest'
import { createSseFrameParser, type SseFrame } from './sse'

const encoder = new TextEncoder()

/** 把整段文本按给定的字节切点喂进去，模拟 ReadableStream 的任意分块。 */
function feed(text: string, cuts: number[] = []): SseFrame[] {
  const bytes = encoder.encode(text)
  const parser = createSseFrameParser()
  const frames: SseFrame[] = []
  const points = [...cuts, bytes.length]
  let from = 0
  for (const to of points) {
    frames.push(...parser.push(bytes.slice(from, to)))
    from = to
  }
  frames.push(...parser.flush())
  return frames
}

describe('SSE 帧解析', () => {
  it('按空行分帧，一块字节里的多个帧一次全出', () => {
    const frames = feed('event: delta\ndata: {"delta":"上午"}\n\nevent: delta\ndata: {"delta":"下午"}\n\n')

    expect(frames).toEqual([
      { event: 'delta', data: '{"delta":"上午"}' },
      { event: 'delta', data: '{"delta":"下午"}' },
    ])
  })

  it('分帧的空行被切在两块之间时不会漏帧', () => {
    const text = 'event: delta\ndata: {"delta":"a"}\n\nevent: done\ndata: {"recap":{}}\n\n'
    // 切点落在第一帧的 "\n\n" 中间
    const cut = encoder.encode('event: delta\ndata: {"delta":"a"}\n').length

    expect(feed(text, [cut])).toEqual([
      { event: 'delta', data: '{"delta":"a"}' },
      { event: 'done', data: '{"recap":{}}' },
    ])
  })

  it('多字节字符被 chunk 切断时拼回原字符', () => {
    const text = 'event: delta\ndata: {"delta":"回忆"}\n\n'
    const bytes = encoder.encode(text)
    // "回" 占 3 字节，切在它中间：任何按字节切分的实现都必须能把它补回来
    const cut = encoder.encode('event: delta\ndata: {"delta":"').length + 1

    expect(feed(text, [cut])).toEqual([{ event: 'delta', data: '{"delta":"回忆"}' }])
    // 逐字节喂也不能出现替换字符（U+FFFD）
    expect(feed(text, [...bytes.keys()].slice(1))).toEqual([{ event: 'delta', data: '{"delta":"回忆"}' }])
  })

  it('注释行被忽略，纯注释块不成帧', () => {
    const frames = feed(': keep-alive\n\n:ka\nevent: delta\ndata: {"delta":"x"}\n\n')

    expect(frames).toEqual([{ event: 'delta', data: '{"delta":"x"}' }])
  })

  it('CRLF、多行 data、缺省 event 都按规范处理', () => {
    const frames = feed('data: 第一行\r\ndata: 第二行\r\n\r\n')

    expect(frames).toEqual([{ event: 'message', data: '第一行\n第二行' }])
  })

  it('CRLF 被切在 CR 与 LF 之间时不会多切出一个空行', () => {
    const text = 'event: ping\r\ndata: {}\r\n\r\nevent: delta\r\ndata: {"delta":"x"}\r\n\r\n'
    const cut = encoder.encode('event: ping\r\ndata: {}\r\n\r').length

    expect(feed(text, [cut])).toEqual([
      { event: 'ping', data: '{}' },
      { event: 'delta', data: '{"delta":"x"}' },
    ])
  })

  it('丢弃没有空行收尾的残帧（流被掐断时宁可少一帧也不要错一帧）', () => {
    expect(feed('event: delta\ndata: {"delta":"半截')).toEqual([])
  })

  it('id / retry 与未知字段不影响帧的形状', () => {
    const frames = feed('id: 7\nretry: 3000\nfoo: bar\nevent: delta\ndata: {"delta":"x"}\n\n')

    expect(frames).toEqual([{ event: 'delta', data: '{"delta":"x"}' }])
  })
})
