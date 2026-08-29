import type {
  IAskingQuestionResponse,
  IKnowledgeOperationDto,
  IKnowledgeProposalResponse,
  IMatcherDto,
  IOperationResultResponse,
  IStrandResponse,
} from '../api/index'

/**
 * 两阶段教学的纯逻辑层（ADR-031 §6，issue 05）：proposal → 可逐项启用/编辑的 review →
 * 选中操作的提交预检与结果摘要。组件层只做取数与渲染，所有可判断的规则都在这里，
 * 便于纯函数测试（本仓库无组件测试基建，见 frontend/CONTEXT.md 惯例）。
 */

// ===== 操作词汇：分区与人话标签 =====

export type OpCategory = 'strand' | 'matcher' | 'episode' | 'probe'

/** 分区展示顺序：Strand 树变更 → 指纹 → 片段事实 → 探针（issue 05 review 分区要求）。 */
export const CATEGORY_ORDER: OpCategory[] = ['strand', 'matcher', 'episode', 'probe']

export const CATEGORY_LABEL: Record<OpCategory, string> = {
  strand: '脉络',
  matcher: '指纹',
  episode: '片段事实',
  probe: '复现探针',
}

export const OP_META: Record<string, { category: OpCategory; label: string }> = {
  createStrand: { category: 'strand', label: '新建脉络' },
  updateStrand: { category: 'strand', label: '编辑脉络' },
  moveStrand: { category: 'strand', label: '移动脉络（纠错）' },
  endStrand: { category: 'strand', label: '结束脉络' },
  bindMatcher: { category: 'matcher', label: '为脉络追加指纹' },
  muteMatcher: { category: 'matcher', label: '静音指纹' },
  createEpisode: { category: 'episode', label: '记录片段事实' },
  updateEpisode: { category: 'episode', label: '编辑片段事实' },
  relateEpisode: { category: 'episode', label: '关联片段事实' },
  promoteEpisode: { category: 'episode', label: '提升为持续脉络' },
  createProbe: { category: 'probe', label: '创建复现探针' },
  resolveProbe: { category: 'probe', label: '解决复现探针' },
}

// ===== Review 状态 =====

/** review 中的一项操作：op 是可编辑的工作副本，enabled 决定是否进入提交。 */
export interface ReviewItem {
  op: IKnowledgeOperationDto
  enabled: boolean
}

export function toReviewItems(proposal: IKnowledgeProposalResponse): ReviewItem[] {
  return (proposal.operations ?? []).map(op => ({ op, enabled: true }))
}

/** 用户确认选中的操作（保持提案顺序——OpId 只能向后引用）。 */
export function selectedOps(items: ReviewItem[]): IKnowledgeOperationDto[] {
  return items.filter(i => i.enabled).map(i => i.op)
}

export function groupByCategory(items: ReviewItem[]): { category: OpCategory; label: string; items: ReviewItem[] }[] {
  return CATEGORY_ORDER
    .map(category => ({
      category,
      label: CATEGORY_LABEL[category],
      items: items.filter(i => OP_META[i.op.type ?? '']?.category === category),
    }))
    .filter(g => g.items.length > 0)
}

// ===== OpId 依赖：取消一项不能悄悄断掉引用它的项 =====

/** 该操作以临时 OpId 引用的同 set 内新建项。 */
export function refTargets(op: IKnowledgeOperationDto): string[] {
  const refs = [
    op.createStrand?.parent?.opId,
    op.moveStrand?.newParent?.opId,
    op.bindMatcher?.strand?.opId,
    op.createEpisode?.relatedStrand?.opId,
    op.relateEpisode?.episode?.opId,
    op.relateEpisode?.relatedStrand?.opId,
    op.createProbe?.episode?.opId,
    op.promoteEpisode?.episode?.opId,
    op.promoteEpisode?.strand?.opId,
  ]
  return refs.filter((r): r is string => !!r)
}

export interface PrecheckResult {
  /** 服务端可预知的约束警告（提交仍以服务端校验为准）。 */
  warnings: string[]
  /** 引用已被取消/删除的新建项的操作——提交必然 unresolved_reference，直接挡下。 */
  blockedOpIds: string[]
}

export function precheck(items: ReviewItem[]): PrecheckResult {
  const enabledIds = new Set(items.filter(i => i.enabled).map(i => i.op.opId ?? ''))
  const warnings: string[] = []
  const blockedOpIds: string[] = []

  for (const item of items) {
    if (!item.enabled) continue
    const op = item.op
    const label = OP_META[op.type ?? '']?.label ?? op.type ?? '操作'

    for (const ref of refTargets(op)) {
      if (!enabledIds.has(ref)) {
        blockedOpIds.push(op.opId ?? '')
        warnings.push(`「${label}」依赖的新建项（${ref}）已被取消——请一并取消它，或恢复被依赖项`)
      }
    }
    if (op.type === 'createStrand' && !op.createStrand?.name?.trim())
      warnings.push('「新建脉络」名字不能为空')
    if (op.type === 'updateStrand' && !op.updateStrand?.name?.trim())
      warnings.push('「编辑脉络」名字不能为空')
    if (op.type === 'createEpisode' && !op.createEpisode?.text?.trim())
      warnings.push('「记录片段事实」内容不能为空')
    if (op.type === 'updateEpisode' && !op.updateEpisode?.text?.trim())
      warnings.push('「编辑片段事实」内容不能为空')
  }
  return { warnings, blockedOpIds }
}

export function canCommit(items: ReviewItem[]): boolean {
  return selectedOps(items).length > 0 && precheck(items).blockedOpIds.length === 0
}

export function findItemIndexByOpId(items: ReviewItem[], opId: string | null | undefined): number {
  if (!opId) return -1
  return items.findIndex(i => i.op.opId === opId)
}

// ===== 已有 Strand 的选择与消歧：按 UUIDv7 提交，展示 path + 有效日期 =====

/** select 选项值编码：'' = 无/顶层；id:<uuid> = 已有节点；op:<opId> = 同 set 内新建项。 */
export function refToValue(ref: { strandId?: string; opId?: string } | undefined): string {
  if (ref?.strandId) return `id:${ref.strandId}`
  if (ref?.opId) return `op:${ref.opId}`
  return ''
}

export function valueToRef(value: string): { strandId?: string; opId?: string } | undefined {
  if (value.startsWith('id:')) return { strandId: value.slice(3) }
  if (value.startsWith('op:')) return { opId: value.slice(3) }
  return undefined
}

export interface RefOption {
  value: string
  label: string
}

/**
 * Strand 引用的可选项：空值（顶层/不关联）→ 同 set 内排在**前面**的新建脉络（OpId 只能
 * 向后引用，提交端约束）→ 已有节点（按 UUIDv7，label 带完整 path + 有效日期消歧）。
 * 已取消的新建项仍列出（标注），防止 select 值悬空——precheck 会挡住这种提交。
 */
export function strandRefOptions(
  items: ReviewItem[], index: number, strands: IStrandResponse[], emptyLabel: string | null,
): RefOption[] {
  const opts: RefOption[] = emptyLabel === null ? [] : [{ value: '', label: emptyLabel }]
  for (let i = 0; i < index; i++) {
    const it = items[i]
    if (it.op.type === 'createStrand' && it.op.opId)
      opts.push({
        value: `op:${it.op.opId}`,
        label: `本次新建 · ${it.op.createStrand?.name ?? ''}${it.enabled ? '' : '（已取消）'}`,
      })
  }
  for (const s of strands) opts.push({ value: `id:${s.id}`, label: strandLabel(s) })
  return opts
}

/** Episode 引用的可选项：同 set 内排在前面的新建片段事实（已有 Episode 由提案直接给 Id，不提供换绑列表）。 */
export function episodeRefOptions(items: ReviewItem[], index: number): RefOption[] {
  const opts: RefOption[] = []
  for (let i = 0; i < index; i++) {
    const it = items[i]
    if (it.op.type === 'createEpisode' && it.op.opId)
      opts.push({
        value: `op:${it.op.opId}`,
        label: `本次记录 · ${it.op.createEpisode?.text ?? ''}${it.enabled ? '' : '（已取消）'}`,
      })
  }
  return opts
}

/** 已有节点 Id → 人话展示（path + 日期）；查不到时回落 Id 本身，绝不按名字猜。 */
export function strandDisplay(strandId: string | undefined, strands: IStrandResponse[]): string {
  const s = strands.find(x => x.id === strandId)
  return s ? strandLabel(s) : strandId ?? ''
}

/**
 * bindMatcher 换绑目标：expectedVersion 必须跟着目标走——换成已有节点时盖上该节点的
 * 读取时版本（沿用旧目标的版本必然 version_conflict / missing_version），换成同 set 内
 * 新建项时清空（由提交端按事务内实际版本补全）。
 */
export function rebindMatcherTarget(
  op: IKnowledgeOperationDto, ref: { strandId?: string; opId?: string }, strands: IStrandResponse[],
): void {
  if (!op.bindMatcher) return
  op.bindMatcher.strand = ref as never
  op.bindMatcher.expectedVersion = ref.strandId
    ? strands.find(s => s.id === ref.strandId)?.version
    : undefined
}

/** Episode 引用的人话展示（review 里"关联/提升哪条片段事实"）。 */
export function episodeRefDisplay(
  ref: { episodeId?: string; opId?: string } | undefined, items: ReviewItem[],
): string {
  if (ref?.opId) {
    const target = items.find(i => i.op.opId === ref.opId)
    return `本次记录 · ${target?.op.createEpisode?.text ?? ref.opId}`
  }
  return ref?.episodeId ?? ''
}

export function dateOnlyLabel(d: Date | undefined): string {
  if (!d) return ''
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

/** 有效日期范围（未知端点视为无界，ADR-031 §2）：同名不同时期靠它消歧。 */
export function dateRangeLabel(startedOn: Date | undefined, endedOn: Date | undefined): string {
  if (!startedOn && !endedOn) return '日期未知'
  const start = startedOn ? dateOnlyLabel(startedOn) : '起点未知'
  const end = endedOn ? dateOnlyLabel(endedOn) : '进行中'
  return `${start} ~ ${end}`
}

/** 已有 Strand 的选择项标签：完整 path + 有效日期。同父同名不同时期由日期区分。 */
export function strandLabel(s: IStrandResponse): string {
  const path = (s.path ?? []).join(' → ') || s.name || ''
  return `${path} · ${dateRangeLabel(s.startedOn, s.endedOn)}`
}

// ===== 日期编辑桥（<input type="date"> ↔ 生成 client 的 Date 字段）=====

export function dateToInput(d: Date | undefined): string {
  return d ? dateOnlyLabel(d) : ''
}

export function inputToDate(s: string): Date | undefined {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(s)) return undefined
  return new Date(`${s}T00:00:00`) // 本地午夜:client 的 formatDate 用本地分量,序列化回同一天
}

// ===== Matcher 与证据卡渲染 =====

const MATCHER_OP_LABEL: Record<string, string> = { equals: '=', prefix: '开头是', contains: '含' }

/** Matcher 的人类可读渲染：`应用 = "livehime" 且 窗口标题 含 "直播"`。读数名经声明标签词典。 */
export function describeMatcher(m: IMatcherDto | undefined, labels: Record<string, string>): string {
  if (!m?.steps?.length) return ''
  return m.steps
    .map(s => `${labels[s.reading ?? ''] ?? s.reading} ${MATCHER_OP_LABEL[s.op ?? ''] ?? s.op} “${s.value}”`)
    .join(' 且 ')
}

/** 证据时段（本地时间）：卡片顶部的"大概时段"。 */
export function formatTimeRange(start: Date | undefined, end: Date | undefined): string {
  if (!start || !end) return ''
  const fmt = (d: Date) => d.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
  return `${fmt(start)} – ${fmt(end)}`
}

export function isRecurrence(q: IAskingQuestionResponse): boolean {
  return q.kind === 'recurrence'
}

// ===== 提交错误的人话映射 =====

/** commit 返回 409 的错误码（并发/库中现状冲突）：提供"重新整理提案"路径而非静默覆盖。 */
export const CONFLICT_CODES = new Set([
  'version_conflict', 'active_children', 'overlap', 'cycle', 'children_outside_range', 'probe_resolved',
])

const ERROR_MESSAGES: Record<string, string> = {
  version_conflict: '这条知识在你审阅期间被改过了——请重新整理提案，基于最新状态再确认',
  active_children: '要结束的脉络还有进行中的子脉络——先处理子级或建立后继',
  overlap: '同名脉络在这个日期范围内已存在——如果是同一件事，请改选已有节点',
  cycle: '不能把脉络移到它自己或后代下面',
  children_outside_range: '日期范围不能小于子脉络的已知范围',
  outside_parent_range: '日期范围不能超出父脉络的已知范围',
  probe_resolved: '这个探针已被解决过了——请重新整理提案',
  not_found: '引用的对象不存在（可能已被删除）——请重新整理提案',
  invalid_name: '名字不能为空',
  invalid_dates: '结束日期不能早于开始日期',
  invalid_text: '内容不能为空',
  invalid_matcher: '指纹谓词无效',
  parent_not_found: '选择的父级不存在',
  empty_changeset: '没有选中任何操作',
  unresolved_reference: '有操作引用了未选中的新建项',
  missing_version: '缺少并发版本——请重新整理提案',
}

export function knowledgeErrorMessage(code: string | undefined, fallback?: string): string {
  return ERROR_MESSAGES[code ?? ''] ?? fallback ?? '保存失败，请重试'
}

// ===== 两阶段各自的失败解释：组件只应用结果，分支在这里可测 =====

export interface ApiErrorLike {
  kind: 'network' | 'http' | 'parse' | 'calendar'
  status?: number
}

export interface ProposeFailure {
  message: string
  /** 证据已过期（服务端 404 question_not_found）：不能带着旧证据重试，只能刷新问题。 */
  expired: boolean
}

/** 提案失败的解释：无论哪种失败，用户的自然语言回答都保留可重试。 */
export function interpretProposeError(err: ApiErrorLike, code: string | undefined): ProposeFailure {
  if (code === 'question_window_mismatch')
    return { expired: true, message: '日期或时区已经变化——请刷新问题后重新核对证据' }
  if (code === 'question_not_found' || err.kind === 'http' && err.status === 404)
    return { expired: true, message: '这张证据卡已过期（问题可能已重新生成）——请刷新后重新核对' }
  if (code === 'generation_failed' || err.kind === 'http' && err.status === 502)
    return { expired: false, message: '整理服务暂不可用，请稍后重试；你的回答已保留' }
  if (err.kind === 'http' && err.status === 400)
    return { expired: false, message: knowledgeErrorMessage(code, '请求无效，请检查回答后重试') }
  if (err.kind === 'network')
    return { expired: false, message: '网络连接失败，请检查网络后重试；你的回答已保留' }
  return { expired: false, message: '整理提案失败，请重试；你的回答已保留' }
}

/**
 * Recap 纠正入口的提案失败解释（issue 06）：与证据卡入口共用分支，但没有"证据卡过期"
 * 语义——纠正的证据是目标日期本身。空日（400 empty_day）意味着那天没有可纠正的观察。
 */
export function interpretCorrectionError(err: ApiErrorLike, code: string | undefined): ProposeFailure {
  if (err.kind === 'http' && err.status === 400 && code === 'empty_day')
    return { expired: false, message: '这一天没有活动记录，没有可纠正的回顾' }
  const failure = interpretProposeError(err, code)
  return { expired: false, message: failure.message } // 目标日期不会"过期"，永远可重试
}

export interface CommitFailure {
  message: string
  /** 失败操作的 OpId（null = set 级失败），用于在 review 里定位标红。 */
  failedOpId: string | null
  /** 并发/库中现状冲突：出口是"重新加载最新知识并重新审阅"，不是原样重试。 */
  conflict: boolean
}

/** 提交失败的解释：验证失败/并发冲突都保留用户编辑；detail 为空时按传输层错误兜底。 */
export function interpretCommitError(
  detail: { failedOpId?: string | null; error?: { code?: string } } | null, err: ApiErrorLike,
): CommitFailure {
  if (detail) {
    const code = detail.error?.code
    return {
      failedOpId: detail.failedOpId ?? null,
      conflict: !!code && CONFLICT_CODES.has(code),
      message: knowledgeErrorMessage(code),
    }
  }
  return {
    failedOpId: null,
    conflict: false,
    message: err.kind === 'network' ? '网络连接失败，提交未生效，请重试' : '提交失败，请重试',
  }
}

// ===== 提交成功回读：真实 UUIDv7/版本/path 的摘要 =====

export function commitSummary(results: IOperationResultResponse[]): string[] {
  return results.map(r => {
    const label = OP_META[r.type ?? '']?.label ?? r.type ?? '操作'
    if (r.promotion) {
      const path = (r.promotion.strand?.path ?? []).join(' → ')
      return `${label}：原片段事实已保留，并关联到「${path}」`
    }
    if (r.strand) {
      const path = (r.strand.path ?? []).join(' → ')
      return `${label}：${path}（v${r.strand.version}）`
    }
    if (r.episode) {
      return `${label}：${dateOnlyLabel(r.episode.localDate)}「${r.episode.text}」`
    }
    if (r.probe) return `${label}：${r.probe.status === 'active' ? '已创建，命中时会再来确认' : '已解决'}`
    return `${label}：完成`
  })
}
