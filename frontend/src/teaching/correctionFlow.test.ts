import { describe, expect, it, vi } from 'vitest'
import type { ICommitChangeSetResponse, IKnowledgeOperationDto } from '../api/index'
import {
  submitCorrection, retryRegenerate, correctionStageHint,
  REGENERATE_FAILED_MESSAGE, type CorrectionDeps,
} from './correctionFlow'

// ===== fixtures =====

const OPS = [{ opId: 'op1', type: 'createEpisode' }] as IKnowledgeOperationDto[]

function commitOk(): ICommitChangeSetResponse {
  return {
    results: [{ opId: 'op1', type: 'createEpisode', episodeId: 'e1' }],
  } as never
}

/** 默认全成功；每个用例只覆盖它关心的那一环。 */
function deps(over: Partial<CorrectionDeps> = {}): CorrectionDeps {
  return {
    commit: vi.fn(async () => commitOk()),
    regenerate: vi.fn(async () => {}),
    toApiError: e => (e as { kind: 'http'; status: number }) ?? { kind: 'network' },
    changeSetErrorOf: () => null,
    ...over,
  }
}

describe('submitCorrection 成功路径', () => {
  it('提交成功后重生成目标日,并回读提交摘要', async () => {
    const d = deps()
    const outcome = await submitCorrection(OPS, d)

    expect(outcome.kind).toBe('done')
    expect(outcome.kind === 'done' && outcome.summary.length).toBeGreaterThan(0)
    expect(d.regenerate).toHaveBeenCalledTimes(1)
  })

  it('顺序是契约：先 commit 再 regenerate', async () => {
    const calls: string[] = []
    const d = deps({
      commit: vi.fn(async () => { calls.push('commit'); return commitOk() }),
      regenerate: vi.fn(async () => { calls.push('regenerate') }),
    })
    await submitCorrection(OPS, d)

    expect(calls).toEqual(['commit', 'regenerate'])
  })
})

describe('提交失败：不触发生成', () => {
  it('知识没写入时 regenerate 绝不被调用', async () => {
    const d = deps({
      commit: vi.fn(async () => { throw { kind: 'http', status: 409 } }),
      changeSetErrorOf: () => ({ failedOpId: 'op1', error: { code: 'version_conflict' } }),
    })
    const outcome = await submitCorrection(OPS, d)

    expect(outcome.kind).toBe('commitFailed')
    expect(d.regenerate).not.toHaveBeenCalled()
  })

  it('冲突失败带上 failedOpId,供 UI 定位到出错的那一项', async () => {
    const d = deps({
      commit: vi.fn(async () => { throw { kind: 'http', status: 409 } }),
      changeSetErrorOf: () => ({ failedOpId: 'op2', error: { code: 'version_conflict' } }),
    })
    const outcome = await submitCorrection(OPS, d)

    expect(outcome.kind === 'commitFailed' && outcome.failure.failedOpId).toBe('op2')
    expect(outcome.kind === 'commitFailed' && outcome.failure.conflict).toBe(true)
  })
})

describe('生成失败：知识保留,回顾不被覆盖', () => {
  it('commit 成功但 regenerate 抛错 → regenerateFailed 且带提交摘要', async () => {
    const d = deps({ regenerate: vi.fn(async () => { throw { kind: 'http', status: 502 } }) })
    const outcome = await submitCorrection(OPS, d)

    expect(outcome.kind).toBe('regenerateFailed')
    expect(outcome.kind === 'regenerateFailed' && outcome.message).toBe(REGENERATE_FAILED_MESSAGE)
    // 已确认的知识不回滚：摘要仍然如实回读
    expect(outcome.kind === 'regenerateFailed' && outcome.summary.length).toBeGreaterThan(0)
  })

  it('生成失败不把错误伪装成提交失败', async () => {
    const d = deps({ regenerate: vi.fn(async () => { throw new Error('boom') }) })
    const outcome = await submitCorrection(OPS, d)

    expect(outcome.kind).not.toBe('commitFailed')
  })
})

describe('retryRegenerate', () => {
  it('重试成功 → done,沿用原提交摘要（知识早已保存）', async () => {
    const outcome = await retryRegenerate(['已创建片段事实'], async () => {})

    expect(outcome.kind).toBe('done')
    expect(outcome.kind === 'done' && outcome.summary).toEqual(['已创建片段事实'])
  })

  it('重试仍失败 → 停在可重试态,不重复提交知识', async () => {
    const regen = vi.fn(async () => { throw new Error('still down') })
    const outcome = await retryRegenerate(['已创建片段事实'], regen)

    expect(outcome.kind).toBe('regenerateFailed')
    expect(regen).toHaveBeenCalledTimes(1)
  })
})

describe('correctionStageHint', () => {
  it('提案阶段明说零写入', () => {
    expect(correctionStageHint('proposing')).toContain('不会写入')
  })

  it('提交阶段诚实说明两件事都在做', () => {
    const hint = correctionStageHint('committing')
    expect(hint).toContain('保存')
    expect(hint).toContain('重新生成')
  })

  it('静默阶段没有提示语', () => {
    expect(correctionStageHint('closed')).toBe('')
    expect(correctionStageHint('done')).toBe('')
  })
})
