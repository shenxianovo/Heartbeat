import { describe, it, expect } from 'vitest'
import { buildTracks } from './timeline/replayModel'
import { urlOf, laneKeyOf, toReplaySegs, toPluginSegs, toSystemSegs } from './segmentAdapters'

const base = new Date(2026, 0, 15, 10, 0, 0)
const later = new Date(base.getTime() + 60_000)

describe('urlOf', () => {
  it('从结构化 Fact payload 读取完整原始 URL', () => {
    expect(urlOf({
      identityKey: 'https://a.com/x', title: 'Page', url: 'https://wrong.example/',
      attributes: { url: 'https://a.com/x?q=original#section' },
    })).toBe('https://a.com/x?q=original#section')
  })

  it.each([null, [], 'bad', { url: 42 }, {}])('无有效 URL 时不伪造网址：%j', attributes => {
    expect(urlOf({ identityKey: 'page', attributes })).toBeUndefined()
  })

  it('缺失 payload 不产生 URL，也不接受旧的顶层属性形状', () => {
    expect(urlOf(undefined)).toBeUndefined()
    expect(urlOf({ url: 'https://wrong.example/' })).toBeUndefined()
  })
})

describe('laneKeyOf', () => {
  it('浏览器使用 schema 声明的 windowId，包含编号 0', () => {
    expect(laneKeyOf('selected-page', { attributes: { windowId: 3 } }, 'stream')).toBe('stream:3')
    expect(laneKeyOf('selected-page', { attributes: { windowId: 0 } }, 'stream')).toBe('stream:0')
  })

  it('其他 source 的通用 laneKey → 稳定泳道', () => {
    expect(laneKeyOf('reference', { attributes: { laneKey: 3 } })).toBe('3')
  })

  it('缺少合法副本身份时装箱兜底', () => {
    expect(laneKeyOf('selected-page', { attributes: { windowId: {} } })).toBeUndefined()
    expect(laneKeyOf('vscode', { attributes: { file: 'a.ts' } })).toBeUndefined()
    expect(laneKeyOf(undefined, { attributes: { laneKey: 1 } })).toBeUndefined()
  })
})

describe('toReplaySegs', () => {
  it('system 在前带标题 label；插件段带 laneKey', () => {
    const segs = toReplaySegs(
      [{ source: 'new.desktop', aspect: 'desktop-activity', appName: 'msedge', title: 'GitHub', startTime: base, endTime: later }],
      [{
        source: 'reference', aspect: 'activity',
        identityKey: 'https://github.com/',
        title: 'GitHub',
        payload: { attributes: { url: 'https://github.com/pulls', laneKey: 7 } },
        startTime: base,
        endTime: later,
      }],
    )
    expect(segs[0].source).toBe('new.desktop')
    expect(segs[0].aspect).toBe('desktop-activity')
    expect(segs[0].label).toBe('GitHub')
    expect(segs[0].laneKey).toBeUndefined()
    expect(segs[1].laneKey).toBe('7')
    expect(segs[1].label).toContain('GitHub')
    expect(segs[1].label).toContain('https://github.com/pulls')
    expect(segs[1].label).not.toContain('laneKey')
  })

  it.each(['native', 'legacy-import', 'missing'])('不同 Browser Stream 的相同窗口编号不会互相遮挡（来源：%s）', origin => {
    const rows = ['profile-a', 'profile-b'].map(streamId => ({
      source: 'browser', aspect: 'selected-page', origin, streamId: origin === 'missing' ? undefined : origin === 'legacy-import' ? 'legacy-stream' : streamId,
      payload: { attributes: { windowId: 1 } }, startTime: base, endTime: later,
    }))
    const tracks = buildTracks(toReplaySegs([], rows), { start: +base, end: +later }, 'UTC')
    expect(tracks[0].lanes).toHaveLength(2)
    expect(tracks[0].lanes.every(lane => lane.bars.length === 1)).toBe(true)
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
  it('url 从 Fact payload 读取，供 labelUpgrade 作副标签', () => {
    const plugins = toPluginSegs([{
      source: 'browser', aspect: 'selected-page',
      identityKey: 'https://a.com/',
      payload: { attributes: { url: 'https://a.com/deep?q=1' } },
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
      { source: 'browser', aspect: 'selected-page', startTime: new Date(start), endTime: new Date(start) },
      { source: 'browser', aspect: 'selected-page', startTime: new Date(end), endTime: new Date(end) },
    ], window)

    expect(system[0]).toEqual(expect.objectContaining({ start, end: start + 30_000 }))
    expect(plugins).toEqual([expect.objectContaining({ start, end: start })])
  })
})
