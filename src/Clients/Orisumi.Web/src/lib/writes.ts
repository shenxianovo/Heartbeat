import type { Observation, Version } from "@/api/types";
import { MINUTE, parseTime } from "./time";

/** 一次写入：两次轮询之间发现的一个 Observation 当前版本。 */
export interface WriteEvent {
  /** 版本的 recordedAt，决定写入画在哪里。 */
  at: number;
  /** 控制台发现它的时刻，决定高亮和波纹从什么时候开始。 */
  seenAt: number;
  entityId: string;
  versionId: string;
  observerVersionId: string;
}

export const WRITE_WINDOW = 10 * MINUTE;

/**
 * 把这一批 Observation 和已知的当前版本比较，返回新出现或版本变化的写入，并更新 `known`。
 * 写入时刻取 recordedAt（晚于 now 时取 now），只返回最近 10 分钟内的写入。
 * 第一次读取时发现的写入不算新发现，seenAt 取写入时刻，打开页面时不会全部高亮。
 */
export function detectWrites(
  known: Map<string, string>,
  versions: Version<Observation>[],
  now: number,
  initial = false,
): WriteEvent[] {
  const writes: WriteEvent[] = [];
  for (const version of versions) {
    if (known.get(version.entityId) === version.versionId) continue;
    known.set(version.entityId, version.versionId);
    const at = Math.min(parseTime(version.recordedAt) ?? now, now);
    if (now - at > WRITE_WINDOW) continue;
    writes.push({
      at,
      seenAt: initial ? at : now,
      entityId: version.entityId,
      versionId: version.versionId,
      observerVersionId: version.content.observerVersionId,
    });
  }
  return writes;
}

export function pruneWrites(writes: WriteEvent[], now: number): WriteEvent[] {
  const cutoff = now - WRITE_WINDOW;
  return writes.filter((w) => w.at >= cutoff);
}
