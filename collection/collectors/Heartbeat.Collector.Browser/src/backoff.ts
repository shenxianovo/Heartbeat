// 上报退避（纯函数）：hub 不可达时按失败次数指数拉开重试间隔，封顶 10 分钟。
// 与"hub 拒收（4xx）"无关——拒收是丢弃不是重试（hub.ts）；将来 403 停用语义（issue 04）走同一退避路径。

/** 基础间隔 = flush 周期（30s）：第一次失败下个周期照常重试，之后指数拉开。 */
export const BACKOFF_BASE_MS = 30_000
export const BACKOFF_MAX_MS = 10 * 60_000

export interface BackoffState {
  fails: number
  nextAttemptAt: number // epoch ms；0 = 无退避
}

export const noBackoff: BackoffState = { fails: 0, nextAttemptAt: 0 }

/** 一次失败后的新退避状态。 */
export function backoffAfterFailure(state: BackoffState, now: number): BackoffState {
  const fails = state.fails + 1
  const delay = Math.min(BACKOFF_BASE_MS * 2 ** (fails - 1), BACKOFF_MAX_MS)
  return { fails, nextAttemptAt: now + delay }
}

/** 当前是否应跳过上报尝试。 */
export function shouldSkipAttempt(state: BackoffState, now: number): boolean {
  return now < state.nextAttemptAt
}
