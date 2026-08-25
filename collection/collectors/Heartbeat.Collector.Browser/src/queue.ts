import type { SegmentSnapshot } from './fold'

type PersistedSegmentSnapshot = Omit<SegmentSnapshot, 'isFinal'> & {
  appName?: unknown
  isFinal?: boolean
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
