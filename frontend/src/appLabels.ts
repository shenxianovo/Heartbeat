/**
 * 合成（伪应用）段标识名，与后端 Heartbeat.Core.SyntheticApps 对应。
 * 这些不是真实应用，而是采集层用来在时间轴上占位的特殊段。详见 ADR-014。
 */
export const AWAY_APP = '__away__'

/** away 判定谓词：全前端唯一的比对点（报表、时间轴、在场面板共用）。 */
export function isAwayName(appName?: string | null): boolean {
  return appName === AWAY_APP
}

/**
 * 应用名 → 文字描述 映射表
 * key 不区分大小写，直接添加新行即可扩展
 */
const labels: Record<string, string> = {
    // 开发
    'Code': '在玩微软大战代码',
    'devenv': '在写代码',
    'datagrip64': '在删库跑路',

    // 终端
    'WindowsTerminal': '在看终端',

    // 游戏
    'VALORANT-Win64-Shipping': '在当瓦学弟',

    // 浏览器
    'chrome': '在上网冲浪',
    'msedge': '在上网冲浪',

    // 社交
    'WeChat': '在水群',
    'QQ': '在水群',
    'Telegram': '在水群',
    'MyPopo': '在上班',

    // 其他
    'Clash for Windows': '在翻墙',
    'explorer': '在找文件',
    'mpv': '在看片',
}

// 不区分大小写
const lookup = new Map<string, string>(
    Object.entries(labels).map(([k, v]) => [k.toLowerCase(), v])
)

export function getAppLabel(appName: string): string | null {
    return lookup.get(appName.toLowerCase()) ?? null
}
