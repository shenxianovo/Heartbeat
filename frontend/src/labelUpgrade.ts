// 回放标签升级（ADR-019）：system 段本按窗口标题切段（IdentityKey = App+Title），
// 切 tab 必改标题，故一个 system 段≈一个页面停留。给每个 system 段找重叠占比最大的
// 插件段，标签从窗口标题升级为页面标题/URL——时长仍取 system 段（统计互斥轨一分不动，
// 升级只是显示效果）。无重叠插件段的段（历史数据、非覆盖时段）走 fallback，
// 按时间窗口自然成立，无需任何"是否装了插件"的全局判断。

export interface TimeSpan {
  start: number // epoch ms
  end: number
}

export interface SystemSeg extends TimeSpan {
  appName?: string
  title?: string
}

export interface PluginSeg extends TimeSpan {
  identityKey?: string
  title?: string
  /** 完整原始 URL（来自 attributes.url），作副标签。 */
  url?: string
}

export interface BreakdownRow {
  title: string
  secondary?: string
  category?: string
  totalSeconds: number
  count: number
  /** true = 由插件段升级而来（页面级）；false = 窗口标题 fallback。 */
  upgraded: boolean
}

/** fallback 格式化器形状，对齐 titleFormatters.formatTitle。 */
export type Fallback = (
  appName: string | undefined,
  title: string | null | undefined,
) => { primary: string; secondary?: string; category?: string }

/** 两区间重叠毫秒数（无重叠为 0）。 */
export function overlapMs(a: TimeSpan, b: TimeSpan): number {
  return Math.max(0, Math.min(a.end, b.end) - Math.max(a.start, b.start))
}

/** 为一个 system 段选重叠最大的插件段；无正重叠返回 null。 */
function bestMatch(seg: SystemSeg, plugins: PluginSeg[]): PluginSeg | null {
  let best: PluginSeg | null = null
  let bestOverlap = 0
  for (const p of plugins) {
    const o = overlapMs(seg, p)
    if (o > bestOverlap) {
      bestOverlap = o
      best = p
    }
  }
  return best
}

/**
 * 升级并聚合某 App 的标题明细。
 * @param systemSegs 该 App 的 system 段
 * @param pluginSegs 同 App 同时间窗的 Collector 段
 * @param fallback 无匹配时的窗口标题格式化器
 */
export function upgradeBreakdown(
  systemSegs: SystemSeg[],
  pluginSegs: PluginSeg[],
  fallback: Fallback,
): BreakdownRow[] {
  const byKey = new Map<string, BreakdownRow>()

  for (const seg of systemSegs) {
    const secs = Math.max(0, Math.round((seg.end - seg.start) / 1000))
    const match = bestMatch(seg, pluginSegs)

    let key: string
    let row: Omit<BreakdownRow, 'totalSeconds' | 'count'>
    if (match) {
      // 页面级：按 identityKey 聚合（同页面多次停留合并），主标签页面标题、副标签 URL。
      key = 'p:' + (match.identityKey ?? match.url ?? match.title ?? '')
      row = {
        title: match.title || match.url || match.identityKey || '未知页面',
        secondary: match.url,
        upgraded: true,
      }
    } else {
      const fmt = fallback(seg.appName, seg.title)
      key = 's:' + fmt.primary
      row = { title: fmt.primary, secondary: fmt.secondary, category: fmt.category, upgraded: false }
    }

    const cur = byKey.get(key)
    if (cur) {
      cur.totalSeconds += secs
      cur.count += 1
    } else {
      byKey.set(key, { ...row, totalSeconds: secs, count: 1 })
    }
  }

  return [...byKey.values()].sort((a, b) => b.totalSeconds - a.totalSeconds)
}
