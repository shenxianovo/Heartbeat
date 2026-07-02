// 展示端标题格式化（无损，仅影响展示聚合，不改采集数据）。详见 ADR-016。
// 每个进程名对应一个 formatter，把原始窗口标题归一化为友好显示。
// 加新规则 = 往 FORMATTERS 里加一条。数据攒多了想细化某个 app，改对应函数即可。

export interface FormattedTitle {
  /** 主显示文本（用于聚合 key 与主标签）。 */
  primary: string
  /** 次要信息（如所属项目/文件夹），可空。 */
  secondary?: string
  /** 归类标记，如 'system' 表示桌面/系统级低价值窗口。 */
  category?: string
}

type TitleFormatter = (raw: string) => FormattedTitle

/**
 * 归一化窗口标题。忠实反映：空标题返回空 primary（由 UI 决定占位显示），
 * 不在此编造 "(无标题)"。空标题也会交给 per-app formatter（如 explorer 空标题归系统）。
 * @param processName 进程名（App 名）
 * @param rawTitle 原始窗口标题
 */
export function formatTitle(processName: string | undefined, rawTitle: string | null | undefined): FormattedTitle {
  const raw = (rawTitle ?? '').trim()

  const fmt = processName ? FORMATTERS[processName.toLowerCase()] : undefined
  if (!fmt) return { primary: raw }

  try {
    const result = fmt(raw)
    // formatter 返回空 primary 时回退到原始（可能也是空，交给 UI）
    return result.primary && result.primary.length > 0 ? result : { primary: raw, category: result.category }
  } catch {
    return { primary: raw }
  }
}

/** 按 " - " 分段（全角/半角空格容错）。VSCode/mpv/Edge 都是这种结构。 */
function splitDash(raw: string): string[] {
  return raw.split(/\s+-\s+/).map(s => s.trim()).filter(s => s.length > 0)
}

// VSCode: "文件 - 项目 - Visual Studio Code"，含 "(工作树) (xxx)" 等 git 装饰。
const vscode: TitleFormatter = (raw) => {
  const parts = splitDash(raw)
  // 末段是应用名（Visual Studio Code），去掉
  if (parts.length && /Visual Studio Code/i.test(parts[parts.length - 1])) parts.pop()
  if (parts.length === 0) return { primary: 'VS Code' }
  // 清掉 VSCode 的 "(工作树) (ConfigManager.cs)" 这类重复装饰后缀
  let file = parts[0].replace(/\s*\(.*\)\s*$/g, '').trim()
  if (file.length === 0) file = parts[0]
  const project = parts.length > 1 ? parts[parts.length - 1] : undefined
  return { primary: file, secondary: project }
}

// mpv: "文件 - mpv"
const mpv: TitleFormatter = (raw) => {
  const parts = splitDash(raw)
  if (parts.length && /^mpv$/i.test(parts[parts.length - 1])) parts.pop()
  return { primary: parts[0] ?? 'mpv' }
}

// WindowsTerminal: "✳ Claude Code" / "⠂ Claude Code" / "⠐ Claude Code" —— 削 spinner 前缀归并。
// 门控挡不住的 spinner 段在此兜底合并（ADR-016）。
const windowsTerminal: TitleFormatter = (raw) => {
  // 去掉开头的非字母数字装饰字符（spinner/图标/emoji）及其后空白
  const stripped = raw.replace(/^[^\p{L}\p{N}]+\s*/u, '').trim()
  return { primary: stripped.length > 0 ? stripped : raw }
}

// explorer: 系统窗口（任务切换/托盘溢出/空标题）归为桌面/系统，真实文件夹名保留。
const EXPLORER_SYSTEM = new Set(['任务切换', '系统托盘溢出窗口。', '系统托盘溢出窗口', ''])
const explorer: TitleFormatter = (raw) => {
  if (EXPLORER_SYSTEM.has(raw.trim())) return { primary: '桌面/系统', category: 'system' }
  return { primary: raw }
}

// Edge: "页面 - 账户 - Microsoft Edge"（Edge 里的空格可能是特殊 U+200B/全角）。取首段页面名。
const edge: TitleFormatter = (raw) => {
  const parts = splitDash(raw)
  if (parts.length && /Microsoft.*Edge/i.test(parts[parts.length - 1])) parts.pop()
  return { primary: parts[0] ?? 'Edge' }
}

/** 进程名（小写）→ formatter。 */
const FORMATTERS: Record<string, TitleFormatter> = {
  code: vscode,
  mpv,
  windowsterminal: windowsTerminal,
  explorer,
  msedge: edge,
}
