import type { ICommitChangeSetResponse, IKnowledgeOperationDto } from '../api/index'
import {
  commitSummary, interpretCommitError, type ApiErrorLike, type CommitFailure,
} from './teachingFlow'

/**
 * Recap 纠正的提交编排（ADR-031 §6/§7，issue 06 纯逻辑层）：
 * 知识事务提交成功后，**才**对目标日期执行一次显式重生成。两个阶段的失败语义不同——
 * 提交失败不生成（知识未写入）；生成失败不回滚已确认的知识、也不覆盖上一版成功 Recap，
 * 只暴露"知识已保存，Recap 尚未更新"并允许单独重试。依赖注入，可纯函数测试。
 */

/** 提交阶段的三种出口：知识与叙事的成败是两件独立的事。 */
export type CorrectionOutcome =
  /** 知识已提交 + 目标日已重生成。 */
  | { kind: 'done'; summary: string[] }
  /** 知识已提交，但重生成失败——上一版 Recap 保留，可单独重试。 */
  | { kind: 'regenerateFailed'; summary: string[]; message: string }
  /** 知识提交失败：整批回滚，未触发任何生成。 */
  | { kind: 'commitFailed'; failure: CommitFailure }

export interface CorrectionDeps {
  /** 共享事务提交端（选中操作全部成功才写入）。 */
  commit: (ops: IKnowledgeOperationDto[]) => Promise<ICommitChangeSetResponse>
  /** 目标日期的显式重生成（流式：用最新 Segment 与知识投影生成并保存新 Recap）。 */
  regenerate: () => Promise<void>
  /** 传输层错误归一（注入以便测试）。 */
  toApiError: (e: unknown) => ApiErrorLike
  /** 提交失败的结构化错误体（failedOpId + code）。 */
  changeSetErrorOf: (e: unknown) => { failedOpId?: string | null; error?: { code?: string } } | null
}

export const REGENERATE_FAILED_MESSAGE = '知识已保存，Recap 尚未更新——可以重试生成'

/**
 * 提交纠正：commit → （仅在成功后）regenerate。
 * 顺序是契约的一部分：commit 抛错时 regenerate 绝不被调用。
 */
export async function submitCorrection(
  ops: IKnowledgeOperationDto[], deps: CorrectionDeps,
): Promise<CorrectionOutcome> {
  let summary: string[]
  try {
    const res = await deps.commit(ops)
    summary = commitSummary(res.results ?? [])
  } catch (e) {
    // 知识没写进去：不生成，保留用户编辑与提案供修复
    return { kind: 'commitFailed', failure: interpretCommitError(deps.changeSetErrorOf(e), deps.toApiError(e)) }
  }

  try {
    await deps.regenerate()
    return { kind: 'done', summary }
  } catch {
    // 已确认的知识不回滚，上一版成功 Recap 不被覆盖
    return { kind: 'regenerateFailed', summary, message: REGENERATE_FAILED_MESSAGE }
  }
}

/** 单独重试重生成（知识已保存的情况下）：成功即收敛为 done，失败仍停在可重试态。 */
export async function retryRegenerate(
  summary: string[], regenerate: () => Promise<void>,
): Promise<CorrectionOutcome> {
  try {
    await regenerate()
    return { kind: 'done', summary }
  } catch {
    return { kind: 'regenerateFailed', summary, message: REGENERATE_FAILED_MESSAGE }
  }
}

/** 纠正面板的阶段（与主动教学同构，多一个"知识已存、待重生成"的出口）。 */
export type CorrectionStage =
  | 'closed'      // 未发起（私有卡片上只有入口按钮）
  | 'writing'     // 用户写自然语言纠正
  | 'proposing'   // LLM 整理中（零写入）
  | 'review'      // 逐项审阅提案
  | 'committing'  // 提交 + 重生成中
  | 'regenerateFailed'
  | 'done'

/** 阶段 → 进行中的提示语（committing 阶段两件事都在做，文案要诚实）。 */
export function correctionStageHint(stage: CorrectionStage): string {
  if (stage === 'proposing') return '正在把你的纠正整理成结构化变更——这一步不会写入任何知识。'
  if (stage === 'committing') return '正在保存知识并重新生成这一天的回顾…'
  return ''
}
