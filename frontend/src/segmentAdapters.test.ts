import { describe, it, expect } from 'vitest'
import { urlOf, laneKeyOf, toReplaySegs, toPluginSegs, toSystemSegs } from './segmentAdapters'

const base = new Date(2026, 0, 15, 10, 0, 0)
const later = new Date(base.getTime() + 60_000)

describe('urlOf', () => {
  it('取合法 attributes.url', () => {
    expect(urlOf('{"url":"https://a.com/x","domain":"a.com"}')).toBe('https://a.com/x')
  })

  it('畸形 JSON / 缺失 / 非字符串一律 undefined', () => {
    expect(urlOf('not json')).toBeUndefined()
    expect(urlOf(undefined)).toBeUndefined()
    expect(urlOf('{"url":42}')).toBeUndefined()
    expect(urlOf('"just a string"')).toBeUndefined()
  })
})

describe('laneKeyOf', () => {
  it('任意 source 的通用 laneKey → 稳定泳道', () => {
    expect(laneKeyOf('reference', '{"laneKey":3}')).toBe('3')
  })

  it('无通用 laneKey → undefined（装箱兜底）', () => {
    expect(laneKeyOf('vscode', '{"file":"a.ts"}')).toBeUndefined()
    expect(laneKeyOf('reference', '{"url":"https://a.com"}')).toBeUndefined()
    expect(laneKeyOf(undefined, '{"laneKey":1}')).toBeUndefined()
  })
})

describe('toReplaySegs', () => {
  it('system 在前带标题 label；插件段带 laneKey', () => {
    const segs = toReplaySegs(
      [{ appName: 'msedge', title: 'GitHub', startTime: base, endTime: later }],
      [{
        source: 'reference',
        identityKey: 'https://github.com/',
        title: 'GitHub',
        attributes: '{"url":"https://github.com/pulls","laneKey":7}',
        startTime: base,
        endTime: later,
      }],
    )
    expect(segs[0].source).toBe('system')
    expect(segs[0].label).toBe('GitHub')
    expect(segs[0].laneKey).toBeUndefined()
    expect(segs[1].laneKey).toBe('7')
    expect(segs[1].label).toContain('GitHub')
    expect(segs[1].label).toContain('laneKey')
  })

  it('缺时间/缺 source 的记录跳过', () => {
    const segs = toReplaySegs(
      [{ appName: 'a', startTime: base }],
      [{ identityKey: 'x', startTime: base, endTime: later }],
    )
    expect(segs).toEqual([])
  })
})

describe('toSystemSegs', () => {
  it('稳定产品 Key 优先于 DisplayName 与 expand 兼容 AppName', () => {
    const [segment] = toSystemSegs([{
      appKey: 'vscode',
      appDisplayName: 'Visual Studio Code',
      appName: 'Code.exe',
      startTime: base,
      endTime: later,
    }])
    expect(segment.appName).toBe('vscode')
  })
})

describe('toPluginSegs', () => {
  it('url 从 attributes 解出，供 labelUpgrade 作副标签', () => {
    const plugins = toPluginSegs([{
      source: 'browser',
      identityKey: 'https://a.com/',
      attributes: '{"url":"https://a.com/deep?q=1"}',
      startTime: base,
      endTime: later,
    }])
    expect(plugins[0].url).toBe('https://a.com/deep?q=1')
  })
})

describe('Local Calendar Window clipping', () => {
  const start = base.getTime()
  const end = later.getTime()
  const window = { start, end }

  it('clips interval rows and preserves the existing start-inclusive point policy', () => {
    const system = toSystemSegs([{
      startTime: new Date(start - 60_000),
      endTime: new Date(start + 30_000),
    }], window)
    const plugins = toPluginSegs([
      { source: 'browser', startTime: new Date(start), endTime: new Date(start) },
      { source: 'browser', startTime: new Date(end), endTime: new Date(end) },
    ], window)

    expect(system[0]).toEqual(expect.objectContaining({ start, end: start + 30_000 }))
    expect(plugins).toEqual([expect.objectContaining({ start, end: start })])
  })
})
