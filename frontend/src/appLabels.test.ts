import { describe, expect, it } from 'vitest'
import { AWAY_APP, isAwayApp } from './appLabels'

describe('isAwayApp', () => {
  it('新 DTO 以稳定产品 Key 为准', () => {
    expect(isAwayApp('away', '已重命名')).toBe(true)
    expect(isAwayApp('not-away', AWAY_APP)).toBe(false)
  })

  it('只在 Key 缺失时回退旧展示串', () => {
    expect(isAwayApp(undefined, AWAY_APP)).toBe(true)
    expect(isAwayApp(undefined, '其他')).toBe(false)
  })
})
