import { describe, expect, it } from 'vitest'
import { detectBrowserAppHint } from '../src/app-hint'

describe('detectBrowserAppHint', () => {
  it.each([
    ['Google Chrome', 'chrome'],
    ['Microsoft Edge', 'edge'],
    ['Brave', 'brave'],
    ['Opera', 'opera'],
    ['Opera GX', 'opera'],
    ['Vivaldi', 'vivaldi'],
    ['Firefox', 'firefox'],
  ] as const)('UA-CH 明确品牌 %s → %s', (brand, expected) => {
    expect(
      detectBrowserAppHint({ brands: ['Not_A Brand', 'Chromium', brand] }),
    ).toBe(expected)
  })

  it.each([
    ['Mozilla/5.0 Edg/126.0', 'edge'],
    ['Mozilla/5.0 OPR/111.0', 'opera'],
    ['Mozilla/5.0 Vivaldi/6.8', 'vivaldi'],
    ['Mozilla/5.0 Firefox/128.0', 'firefox'],
  ] as const)('专属 UA token 可可靠识别 %s', (userAgent, expected) => {
    expect(detectBrowserAppHint({ userAgent })).toBe(expected)
  })

  it('Brave 专属 API 可在 UA-CH 缺席时识别', () => {
    expect(detectBrowserAppHint({ userAgent: 'Mozilla/5.0 Chrome/126.0', hasBraveApi: true })).toBe(
      'brave',
    )
  })

  it('只有通用 Chromium/Chrome UA 时不默认猜 Chrome', () => {
    expect(
      detectBrowserAppHint({
        brands: ['Not;A=Brand', 'Chromium'],
        userAgent: 'Mozilla/5.0 Chrome/126.0 Safari/537.36',
      }),
    ).toBeUndefined()
  })

  it('未知 UA-CH 品牌即省略 hint', () => {
    expect(detectBrowserAppHint({ brands: ['Chromium', 'Acme Browser'] })).toBeUndefined()
  })

  it('多个互相冲突的明确品牌即省略 hint', () => {
    expect(detectBrowserAppHint({ brands: ['Google Chrome', 'Microsoft Edge'] })).toBeUndefined()
  })

  it('重复的同一逻辑品牌不构成歧义', () => {
    expect(detectBrowserAppHint({ brands: ['Opera', 'Opera GX'] })).toBe('opera')
  })
})
