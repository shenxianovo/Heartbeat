import { describe, expect, it } from 'vitest'
import {
  BACKOFF_BASE_MS,
  BACKOFF_MAX_MS,
  backoffAfterFailure,
  noBackoff,
  shouldSkipAttempt,
} from '../src/backoff'

const NOW = 1_000_000

describe('backoff', () => {
  it('无退避状态不跳过尝试', () => {
    expect(shouldSkipAttempt(noBackoff, NOW)).toBe(false)
  })

  it('失败次数指数拉开间隔：30s → 60s → 120s', () => {
    const f1 = backoffAfterFailure(noBackoff, NOW)
    expect(f1.nextAttemptAt - NOW).toBe(BACKOFF_BASE_MS)

    const f2 = backoffAfterFailure(f1, NOW)
    expect(f2.nextAttemptAt - NOW).toBe(BACKOFF_BASE_MS * 2)

    const f3 = backoffAfterFailure(f2, NOW)
    expect(f3.nextAttemptAt - NOW).toBe(BACKOFF_BASE_MS * 4)
  })

  it('间隔封顶 10 分钟', () => {
    let s = noBackoff
    for (let i = 0; i < 20; i++) s = backoffAfterFailure(s, NOW)
    expect(s.nextAttemptAt - NOW).toBe(BACKOFF_MAX_MS)
  })

  it('退避窗口内跳过，窗口过后放行', () => {
    const s = backoffAfterFailure(noBackoff, NOW)
    expect(shouldSkipAttempt(s, NOW + BACKOFF_BASE_MS - 1)).toBe(true)
    expect(shouldSkipAttempt(s, NOW + BACKOFF_BASE_MS)).toBe(false)
  })
})
