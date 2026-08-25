import type { SegmentSnapshot } from './fold'

type PersistedSegmentSnapshot = Omit<SegmentSnapshot, 'isFinal'> & {
  appName?: unknown
  isFinal?: boolean
}

export const MAX_QUEUED = 5_000

export function enqueueBounded(
  current: Record<string, SegmentSnapshot>,
  snapshots: SegmentSnapshot[],
  limit = MAX_QUEUED,
): { queue: Record<string, SegmentSnapshot>; overflow: SegmentSnapshot[] } {
  const queue = { ...current }
  const overflow: SegmentSnapshot[] = []
  let queuedCount = Object.keys(queue).length
  for (const snapshot of snapshots) {
    if (queue[snapshot.id] === undefined && queuedCount >= limit) {
      overflow.push(snapshot)
    } else {
      if (queue[snapshot.id] === undefined) queuedCount += 1
      queue[snapshot.id] = snapshot
    }
  }
  return { queue, overflow }
}

/**
 * 把旧扩展留下的 Windows appName 队列提升到 appHint 契约。
 * 品牌不明确时只移除旧字段，段的其余事实仍原样重放。
 */
export function normalizeQueuedSnapshots(
  stored: Record<string, PersistedSegmentSnapshot>,
  currentAppHint: string | undefined,
): Record<string, SegmentSnapshot> {
  return Object.fromEntries(
    Object.entries(stored).map(([id, { appName: _legacyAppName, ...snapshot }]) => [
      id,
      {
        ...snapshot,
        isFinal: snapshot.isFinal === true,
        ...(snapshot.appHint === undefined && currentAppHint !== undefined
          ? { appHint: currentAppHint }
          : {}),
      },
    ]),
  )
}
