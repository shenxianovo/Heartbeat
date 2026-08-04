import { describe, expect, it } from 'vitest'
import type { IKnowledgeOperationDto, IKnowledgeProposalResponse, IStrandResponse } from '../api/index'
import {
  toReviewItems, selectedOps, groupByCategory, precheck, canCommit,
  strandRefOptions, episodeRefOptions, strandDisplay, episodeRefDisplay, rebindMatcherTarget,
  refToValue, valueToRef, dateToInput, inputToDate, dateRangeLabel, strandLabel,
  describeMatcher, isRecurrence, knowledgeErrorMessage, commitSummary, CONFLICT_CODES,
  interpretProposeError, interpretCommitError, interpretCorrectionError, findItemIndexByOpId,
  type ReviewItem,
} from './teachingFlow'

// ===== fixtures =====

function op(partial: Partial<IKnowledgeOperationDto> & { opId: string; type: string }): IKnowledgeOperationDto {
  return partial as IKnowledgeOperationDto
}

function createStrandOp(opId: string, name = '花生', parentOpId?: string): IKnowledgeOperationDto {
  return op({
    opId,
    type: 'createStrand',
    createStrand: {
      name,
      gloss: '',
      parent: parentOpId ? { opId: parentOpId } : undefined,
      members: [],
    } as never,
  })
}

function createEpisodeOp(opId: string, text = '做了产品调研', relatedOpId?: string): IKnowledgeOperationDto {
  return op({
    opId,
    type: 'createEpisode',
    createEpisode: {
      localDate: new Date('2026-08-03T00:00:00'),
      text,
      relatedStrand: relatedOpId ? { opId: relatedOpId } : undefined,
    } as never,
  })
}

function proposal(operations: IKnowledgeOperationDto[]): IKnowledgeProposalResponse {
  return { explanation: '', operations, warnings: [], suggestions: [], readingLabels: {} } as never
}

function strand(id: string, path: string[], startedOn?: string, endedOn?: string): IStrandResponse {
  return {
    id,
    name: path[path.length - 1],
    gloss: '',
    path,
    version: 3,
    members: [],
    startedOn: startedOn ? new Date(`${startedOn}T00:00:00`) : undefined,
    endedOn: endedOn ? new Date(`${endedOn}T00:00:00`) : undefined,
  } as never
}

function items(...ops: IKnowledgeOperationDto[]): ReviewItem[] {
  return toReviewItems(proposal(ops))
}

// ===== review 状态与选中 =====

describe('toReviewItems / selectedOps', () => {
  it('提案的每个操作默认启用,选中集保持提案顺序', () => {
    const list = items(createStrandOp('op1'), createEpisodeOp('op2'))
    expect(list.every(i => i.enabled)).toBe(true)
    expect(selectedOps(list).map(o => o.opId)).toEqual(['op1', 'op2'])
  })

  it('取消的操作不进入提交集(未确认项不得提交)', () => {
    const list = items(createStrandOp('op1'), createEpisodeOp('op2'))
    list[0].enabled = false
    expect(selectedOps(list).map(o => o.opId)).toEqual(['op2'])
  })
})

describe('groupByCategory', () => {
  it('按 Strand/Matcher/Episode/Probe 分区,空分区不出现', () => {
    const list = items(
      createEpisodeOp('op1'),
      createStrandOp('op2'),
      op({ opId: 'op3', type: 'createProbe', createProbe: { episode: { opId: 'op1' }, matcher: { source: 'system', steps: [] } } as never }),
    )
    const groups = groupByCategory(list)
    expect(groups.map(g => g.category)).toEqual(['strand', 'episode', 'probe'])
    expect(groups[0].items[0].op.opId).toBe('op2')
  })
})

// ===== 逐项取消的依赖预检 =====

describe('precheck', () => {
  it('取消被依赖的新建项时,引用它的操作被挡下并给出警告', () => {
    const list = items(createStrandOp('op1'), createEpisodeOp('op2', '当天事实', 'op1'))
    list[0].enabled = false
    const result = precheck(list)
    expect(result.blockedOpIds).toEqual(['op2'])
    expect(result.warnings.length).toBeGreaterThan(0)
    expect(canCommit(list)).toBe(false)
  })

  it('依赖项恢复启用后解除阻塞', () => {
    const list = items(createStrandOp('op1'), createEpisodeOp('op2', '当天事实', 'op1'))
    list[0].enabled = false
    list[0].enabled = true
    expect(precheck(list).blockedOpIds).toEqual([])
    expect(canCommit(list)).toBe(true)
  })

  it('连同依赖方一起取消则不再阻塞(两项都不提交)', () => {
    const list = items(createStrandOp('op1'), createEpisodeOp('op2', '当天事实', 'op1'))
    list[0].enabled = false
    list[1].enabled = false
    expect(precheck(list).blockedOpIds).toEqual([])
    expect(canCommit(list)).toBe(false) // 全取消也不能提交空集
  })

  it('promoteEpisode 同时依赖 episode 与 strand 的 OpId 引用', () => {
    const list = items(
      createEpisodeOp('op1'),
      createStrandOp('op2'),
      op({ opId: 'op3', type: 'promoteEpisode', promoteEpisode: { episode: { opId: 'op1' }, strand: { opId: 'op2' }, bindProbeMatcher: false } as never }),
    )
    list[1].enabled = false
    expect(precheck(list).blockedOpIds).toEqual(['op3'])
  })

  it('编辑清空名字/文本时给出警告', () => {
    const list = items(createStrandOp('op1', '  '), createEpisodeOp('op2', ''))
    const result = precheck(list)
    expect(result.warnings.some(w => w.includes('名字不能为空'))).toBe(true)
    expect(result.warnings.some(w => w.includes('内容不能为空'))).toBe(true)
  })

  it('引用已有对象(UUIDv7)不受取消预检影响', () => {
    const list = items(op({
      opId: 'op1',
      type: 'bindMatcher',
      bindMatcher: { strand: { strandId: '0198aaaa-0000-7000-8000-000000000001' }, expectedVersion: 2, matcher: { source: 'system', steps: [] } } as never,
    }))
    expect(precheck(list).blockedOpIds).toEqual([])
  })
})

describe('rebindMatcherTarget', () => {
  const existing = [strand('id-a', ['哔哩哔哩实习'])]

  function bindOp(): IKnowledgeOperationDto {
    return op({
      opId: 'op1',
      type: 'bindMatcher',
      bindMatcher: { strand: { strandId: 'id-old' }, expectedVersion: 7, matcher: { source: 'system', steps: [] } } as never,
    })
  }

  it('换绑到已有节点时 expectedVersion 盖上该节点的读取时版本', () => {
    const o = bindOp()
    rebindMatcherTarget(o, { strandId: 'id-a' }, existing)
    expect(o.bindMatcher?.strand).toMatchObject({ strandId: 'id-a' })
    expect(o.bindMatcher?.expectedVersion).toBe(3)
  })

  it('换绑到同 set 新建项时清空版本(由提交端按事务内实际版本补全)', () => {
    const o = bindOp()
    rebindMatcherTarget(o, { opId: 'op0' }, existing)
    expect(o.bindMatcher?.strand).toMatchObject({ opId: 'op0' })
    expect(o.bindMatcher?.expectedVersion).toBeUndefined()
  })
})

describe('findItemIndexByOpId', () => {
  it('按 failedOpId 定位到对应操作', () => {
    const list = items(createStrandOp('op1'), createEpisodeOp('op2'))
    expect(findItemIndexByOpId(list, 'op2')).toBe(1)
    expect(findItemIndexByOpId(list, 'op9')).toBe(-1)
    expect(findItemIndexByOpId(list, null)).toBe(-1)
  })
})

// ===== 已有 Strand 消歧:path + 日期,按 Id 提交 =====

describe('strandLabel / dateRangeLabel', () => {
  it('同名不同时期靠 path 与有效日期区分', () => {
    const a = strand('id-a', ['哔哩哔哩实习', '花生'], '2025-07-01', '2025-09-30')
    const b = strand('id-b', ['哔哩哔哩实习', '花生'], '2026-07-01')
    expect(strandLabel(a)).toBe('哔哩哔哩实习 → 花生 · 2025-07-01 ~ 2025-09-30')
    expect(strandLabel(b)).toBe('哔哩哔哩实习 → 花生 · 2026-07-01 ~ 进行中')
    expect(strandLabel(a)).not.toBe(strandLabel(b))
  })

  it('未知端点视为无界', () => {
    expect(dateRangeLabel(undefined, undefined)).toBe('日期未知')
    expect(dateRangeLabel(undefined, new Date('2026-01-01T00:00:00'))).toBe('起点未知 ~ 2026-01-01')
  })
})

describe('strandRefOptions', () => {
  const existing = [strand('id-a', ['哔哩哔哩实习'])]

  it('选项 = 空值 + 排在前面的新建脉络(OpId) + 已有节点(UUIDv7)', () => {
    const list = items(createStrandOp('op1', '实习'), createStrandOp('op2', '花生'))
    const opts = strandRefOptions(list, 1, existing, '顶层(无父级)')
    expect(opts.map(o => o.value)).toEqual(['', 'op:op1', 'id:id-a'])
  })

  it('排在后面的新建项不可引用(OpId 只能向后引用)', () => {
    const list = items(createStrandOp('op1'), createStrandOp('op2'))
    const opts = strandRefOptions(list, 0, [], null)
    expect(opts).toEqual([])
  })

  it('已取消的新建项仍列出并标注,防止 select 值悬空', () => {
    const list = items(createStrandOp('op1', '实习'), createStrandOp('op2'))
    list[0].enabled = false
    const opts = strandRefOptions(list, 1, [], null)
    expect(opts[0].label).toContain('（已取消）')
  })
})

describe('refToValue / valueToRef 往返', () => {
  it('id/op/空 三种取值', () => {
    expect(valueToRef(refToValue({ strandId: 'abc' }))).toEqual({ strandId: 'abc' })
    expect(valueToRef(refToValue({ opId: 'op1' }))).toEqual({ opId: 'op1' })
    expect(valueToRef(refToValue(undefined))).toBeUndefined()
  })
})

describe('strandDisplay / episodeRefDisplay', () => {
  it('已有节点按 Id 查 path,查不到回落 Id 而不是按名字猜', () => {
    const existing = [strand('id-a', ['哔哩哔哩实习', '花生'], '2026-07-01')]
    expect(strandDisplay('id-a', existing)).toContain('哔哩哔哩实习 → 花生')
    expect(strandDisplay('id-missing', existing)).toBe('id-missing')
  })

  it('OpId 引用显示新建片段事实的文本', () => {
    const list = items(createEpisodeOp('op1', '看了 Hyperframes'))
    expect(episodeRefDisplay({ opId: 'op1' }, list)).toBe('本次记录 · 看了 Hyperframes')
    expect(episodeRefDisplay({ episodeId: 'ep-1' }, list)).toBe('ep-1')
  })
})

describe('episodeRefOptions', () => {
  it('只列出排在前面的新建片段事实', () => {
    const list = items(createEpisodeOp('op1', '事实A'), createStrandOp('op2'), createEpisodeOp('op3', '事实B'))
    expect(episodeRefOptions(list, 2).map(o => o.value)).toEqual(['op:op1'])
  })
})

// ===== 日期编辑桥 =====

describe('dateToInput / inputToDate', () => {
  it('yyyy-MM-dd 往返保持同一天(本地分量)', () => {
    const d = inputToDate('2026-08-03')!
    expect(dateToInput(d)).toBe('2026-08-03')
  })

  it('非法输入回 undefined(= 未知端点)', () => {
    expect(inputToDate('')).toBeUndefined()
    expect(inputToDate('2026/08/03')).toBeUndefined()
  })
})

// ===== 证据卡渲染 =====

describe('describeMatcher / isRecurrence', () => {
  it('读数名经声明标签词典,缺失回落原名', () => {
    const m = { source: 'system', steps: [{ reading: 'app', op: 'equals', value: 'livehime' }, { reading: 'title', op: 'contains', value: '直播' }] } as never
    expect(describeMatcher(m, { app: '应用' })).toBe('应用 = “livehime” 且 title 含 “直播”')
  })

  it('kind 区分 cluster 与 recurrence', () => {
    expect(isRecurrence({ kind: 'recurrence' } as never)).toBe(true)
    expect(isRecurrence({ kind: 'cluster' } as never)).toBe(false)
  })
})

// ===== 提交错误与回读 =====

describe('knowledgeErrorMessage / CONFLICT_CODES', () => {
  it('并发冲突码有人话文案且被识别为冲突', () => {
    for (const code of ['version_conflict', 'active_children', 'overlap', 'probe_resolved']) {
      expect(CONFLICT_CODES.has(code)).toBe(true)
      expect(knowledgeErrorMessage(code)).not.toBe('保存失败，请重试')
    }
  })

  it('未知码回落兜底文案', () => {
    expect(knowledgeErrorMessage('whatever')).toBe('保存失败，请重试')
    expect(knowledgeErrorMessage(undefined, '自定义')).toBe('自定义')
  })
})

describe('interpretProposeError', () => {
  it('404 = 证据已过期,只能刷新问题,不能带旧证据重试', () => {
    const f = interpretProposeError({ kind: 'http', status: 404 }, 'question_not_found')
    expect(f.expired).toBe(true)
    expect(f.message).toContain('过期')
  })

  it('502(LLM 失败)/网络失败提示回答已保留,不算过期', () => {
    for (const err of [{ kind: 'http' as const, status: 502 }, { kind: 'network' as const }]) {
      const f = interpretProposeError(err, undefined)
      expect(f.expired).toBe(false)
      expect(f.message).toContain('回答已保留')
    }
  })

  it('400 按错误码出人话,无码有兜底', () => {
    expect(interpretProposeError({ kind: 'http', status: 400 }, 'empty_answer').message).toBeTruthy()
    expect(interpretProposeError({ kind: 'parse' }, undefined).message).toContain('重试')
  })
})

describe('interpretCorrectionError', () => {
  it('空日：这一天没有可纠正的观察', () => {
    const f = interpretCorrectionError({ kind: 'http', status: 400 }, 'empty_day')
    expect(f.expired).toBe(false)
    expect(f.message).toContain('没有活动记录')
  })

  it('目标日期不会过期：即使 404 也允许原样重试', () => {
    // 与证据卡入口的关键差别——纠正的证据是日期本身,不是一张会失效的卡
    expect(interpretCorrectionError({ kind: 'http', status: 404 }, 'question_not_found').expired).toBe(false)
  })

  it('LLM/网络失败复用共享文案', () => {
    expect(interpretCorrectionError({ kind: 'http', status: 502 }, undefined).message).toBeTruthy()
    expect(interpretCorrectionError({ kind: 'network' }, undefined).expired).toBe(false)
  })
})

describe('interpretCommitError', () => {
  it('version_conflict 识别为并发冲突并定位 failedOpId', () => {
    const f = interpretCommitError({ failedOpId: 'op2', error: { code: 'version_conflict' } }, { kind: 'http', status: 409 })
    expect(f.conflict).toBe(true)
    expect(f.failedOpId).toBe('op2')
    expect(f.message).toContain('重新整理提案')
  })

  it('验证失败(400)不算冲突但仍定位操作', () => {
    const f = interpretCommitError({ failedOpId: 'op1', error: { code: 'invalid_name' } }, { kind: 'http', status: 400 })
    expect(f.conflict).toBe(false)
    expect(f.failedOpId).toBe('op1')
    expect(f.message).toBe('名字不能为空')
  })

  it('set 级失败 failedOpId 为 null', () => {
    const f = interpretCommitError({ failedOpId: null, error: { code: 'empty_changeset' } }, { kind: 'http', status: 400 })
    expect(f.failedOpId).toBeNull()
  })

  it('无结构化错误体时按传输层兜底,网络失败明说未生效', () => {
    expect(interpretCommitError(null, { kind: 'network' }).message).toContain('未生效')
    expect(interpretCommitError(null, { kind: 'http', status: 500 }).message).toContain('提交失败')
  })
})

describe('commitSummary', () => {
  it('Strand 回读展示真实 path 与版本,提升说明原片段保留', () => {
    const lines = commitSummary([
      { opId: 'op1', type: 'createStrand', strand: { path: ['哔哩哔哩实习', '花生'], version: 1 } },
      { opId: 'op2', type: 'promoteEpisode', promotion: { strand: { path: ['花生'] }, episode: {} } },
    ] as never)
    expect(lines[0]).toContain('哔哩哔哩实习 → 花生')
    expect(lines[0]).toContain('v1')
    expect(lines[1]).toContain('原片段事实已保留')
  })

  it('Episode 回读带日期与文本', () => {
    const lines = commitSummary([
      { opId: 'op1', type: 'createEpisode', episode: { localDate: new Date('2026-08-03T00:00:00'), text: '产品调研' } },
    ] as never)
    expect(lines[0]).toContain('2026-08-03')
    expect(lines[0]).toContain('产品调研')
  })
})
