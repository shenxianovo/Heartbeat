/** 当前浏览器会话中的一个窗口是观测对象；URL、标题与活动判据是它的读数。 */
export interface WindowActivity {
  windowId: number
  identityKey: string
  url: string
  title: string
}

export type WindowObservation =
  | { kind: 'activated'; windowId: number; url: string; title: string }
  | { kind: 'windowClosed'; windowId: number }

type WindowActivityChange =
  | { kind: 'closed' }
  | { kind: 'started' | 'updated'; activity: WindowActivity }

/**
 * 判定一个窗口内的页面活动变化，不生成 Fact 身份、时间或快照。
 * 调用方按 windowId 提供当前活动；窗口关闭后移除它，下次出现即开始新活动。
 */
export function observeWindow(
  current: WindowActivity | undefined,
  observation: WindowObservation,
  identityKeyOf: (url: string) => string,
): WindowActivityChange {
  if (observation.kind === 'windowClosed') return { kind: 'closed' }
  const activity: WindowActivity = {
    windowId: observation.windowId,
    identityKey: identityKeyOf(observation.url),
    url: observation.url,
    title: observation.title,
  }
  // 同一页面的标题、追踪参数等变化只更新读数；页面判据变化才开始另一项活动。
  return { kind: current?.identityKey === activity.identityKey ? 'updated' : 'started', activity }
}
