import { describe, expect, it } from 'vitest'
import type { SegmentSnapshot } from '../src/fold'
import { normalizeQueuedSnapshots } from '../src/queue'

function legacySnapshot(): SegmentSnapshot & { appName: string } {
  return {
    id: 's1',
    source: 'browser',
    identityKey: 'https://example.com/page',
    appName: 'msedge',
    title: 'Example',
    startTime: '2026-08-11T00:00:00.000Z',
    endTime: '2026-08-11T00:01:00.000Z',
    attributes: {
      url: 'https://example.com/page?q=1',
      domain: 'example.com',
      site: 'example.com',
      windowId: 7,
    },
  }
}

describe('normalizeQueuedSnapshots', () => {
  it('旧 appName 队列移除平台字段，并补当前可靠 appHint', () => {
    const result = normalizeQueuedSnapshots({ s1: legacySnapshot() }, 'edge')

    expect(result.s1).not.toHaveProperty('appName')
    expect(result.s1).toMatchObject({
      id: 's1',
      source: 'browser',
      identityKey: 'https://example.com/page',
      appHint: 'edge',
      attributes: { windowId: 7 },
    })
  })

  it('品牌未知时只移除 appName，不丢段也不猜 hint', () => {
    const result = normalizeQueuedSnapshots({ s1: legacySnapshot() }, undefined)

    expect(result.s1).not.toHaveProperty('appName')
    expect(result.s1).not.toHaveProperty('appHint')
    expect(result.s1.identityKey).toBe('https://example.com/page')
  })

  it('已有 appHint 保持原值，不被当前检测结果覆盖', () => {
    const stored = { ...legacySnapshot(), appHint: 'chrome' }
    expect(normalizeQueuedSnapshots({ s1: stored }, 'edge').s1.appHint).toBe('chrome')
  })
})
