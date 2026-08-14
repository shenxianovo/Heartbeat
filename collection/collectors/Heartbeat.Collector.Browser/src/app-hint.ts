/**
 * Collector 只报告平台无关的产品提示；win:/mac: 身份由本机 hub 的平台 resolver 决定。
 *
 * Chromium 系浏览器共享大量 UA 标记，所以这里只接受明确品牌信号。未知品牌、互相冲突的
 * 多品牌信号或只有通用 Chromium 标记时均返回 undefined，避免把证据不足的浏览器猜成 Chrome。
 */

export type BrowserAppHint =
  | 'chrome'
  | 'edge'
  | 'brave'
  | 'opera'
  | 'vivaldi'
  | 'firefox'

export interface BrowserBrandSignals {
  /** User-Agent Client Hints 的 brands；未实现该 API 时可省略。 */
  brands?: readonly string[]
  /** 只用于有稳定专属 token 的浏览器；不会用通用 Chrome/Safari token 猜品牌。 */
  userAgent?: string
  /** Brave 暴露的 navigator.brave 是比通用 Chromium UA 更明确的信号。 */
  hasBraveApi?: boolean
}

const EXACT_BRANDS = new Map<string, BrowserAppHint>([
  ['google chrome', 'chrome'],
  ['microsoft edge', 'edge'],
  ['brave', 'brave'],
  ['opera', 'opera'],
  ['opera gx', 'opera'],
  ['vivaldi', 'vivaldi'],
  ['firefox', 'firefox'],
])

export function detectBrowserAppHint(signals: BrowserBrandSignals): BrowserAppHint | undefined {
  const candidates = new Set<BrowserAppHint>()
  let hasUnknownBrand = false

  for (const rawBrand of signals.brands ?? []) {
    const brand = rawBrand.trim().toLowerCase()
    const exact = EXACT_BRANDS.get(brand)
    if (exact) {
      candidates.add(exact)
    } else if (!isGenericClientHintBrand(brand)) {
      hasUnknownBrand = true
    }
  }

  if (signals.hasBraveApi) candidates.add('brave')

  const ua = signals.userAgent ?? ''
  if (/\bEdg(?:A|iOS)?\//i.test(ua)) candidates.add('edge')
  if (/\bOPR\//i.test(ua)) candidates.add('opera')
  if (/\bVivaldi\//i.test(ua)) candidates.add('vivaldi')
  if (/\bFirefox\//i.test(ua)) candidates.add('firefox')

  if (hasUnknownBrand || candidates.size !== 1) return undefined
  return candidates.values().next().value
}

function isGenericClientHintBrand(brand: string): boolean {
  if (brand === '' || brand === 'chromium') return true

  // UA-CH 会故意加入形如 "Not_A Brand" / "Not;A=Brand" 的 GREASE 项。
  return brand.replace(/[^a-z0-9]/g, '') === 'notabrand'
}
