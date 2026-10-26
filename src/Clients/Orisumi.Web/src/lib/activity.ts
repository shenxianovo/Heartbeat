import type { Observation, Version } from "@/api/types";
import type { CategoryKey } from "./categories";
import { nameObservers, type ObserverIdentity } from "./observers";
import { MINUTE, parseTime, SECOND } from "./time";
import { detectWrites, pruneWrites, type WriteEvent } from "./writes";

export const ACTIVE_WINDOW = MINUTE;

/** 表盘上的一段：一份 Observation 的已知边界。 */
export interface Segment {
  entityId: string;
  start: number | null;
  end: number | null;
  recordedAt: number;
}

export interface ObserverActivity {
  key: string;
  entityId: string | null;
  name: string;
  category: CategoryKey;
  /** 最近 10 分钟的写入时刻，升序。 */
  writes: number[];
  /** 与 writes 一一对应的发现时刻。 */
  seen: number[];
  lastSeenAt: number;
  lastActivityAt: number;
  observationCount: number;
  segments: Segment[];
}

export interface ActivitySnapshot {
  revision: number;
  observers: ObserverActivity[];
  observationCount: number;
}

export function isActive(observer: ObserverActivity, now: number): boolean {
  return now - observer.lastActivityAt < ACTIVE_WINDOW;
}

/**
 * 汇总今天的 Observation 和最近 10 分钟的写入。
 * 轮询结果交给 ingestToday / ingestLive，视图每帧读 snapshot()。
 */
export class ActivityStore {
  private readonly today = new Map<string, Version<Observation>>();
  private readonly known = new Map<string, string>();
  private writes: WriteEvent[] = [];
  private readonly identities = new Map<string, ObserverIdentity>();
  private readonly pending = new Set<string>();
  private readonly listeners = new Set<() => void>();
  private cached: ActivitySnapshot | null = null;
  private cachedAt = 0;
  /** 今天的数据第一次读到之前，发现的写入都算打开页面前已有的写入。 */
  private seeded = false;
  private revision = 0;
  private readonly resolve: (versionId: string) => Promise<ObserverIdentity>;

  constructor(resolve: (versionId: string) => Promise<ObserverIdentity>) {
    this.resolve = resolve;
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getRevision = (): number => this.revision;

  /** 今天的完整结果：替换今天的数据。 */
  ingestToday(versions: Version<Observation>[], now: number): void {
    this.today.clear();
    for (const version of versions) this.today.set(version.entityId, version);
    this.record(versions, now);
    this.seeded = true;
  }

  /** “现在”窗口的结果：合并进今天的数据。 */
  ingestLive(versions: Version<Observation>[], now: number): void {
    for (const version of versions) this.today.set(version.entityId, version);
    this.record(versions, now);
  }

  snapshot(now: number): ActivitySnapshot {
    // 没有新数据时每秒重算一次，让超过 10 分钟的写入移出。
    if (this.cached && this.cached.revision === this.revision && now - this.cachedAt < SECOND) return this.cached;
    this.cachedAt = now;
    this.writes = pruneWrites(this.writes, now);

    const groups = new Map<
      string,
      { identity: ObserverIdentity; versions: Version<Observation>[]; writes: WriteEvent[] }
    >();
    const group = (observerVersionId: string) => {
      const identity = this.identities.get(observerVersionId);
      if (!identity) return null;
      let entry = groups.get(identity.key);
      if (!entry) {
        entry = { identity, versions: [], writes: [] };
        groups.set(identity.key, entry);
      }
      return entry;
    };
    for (const version of this.today.values()) group(version.content.observerVersionId)?.versions.push(version);
    for (const write of this.writes) group(write.observerVersionId)?.writes.push(write);

    const names = nameObservers([...groups.values()].map((g) => g.identity));
    const observers = [...groups.values()].map(({ identity, versions, writes }): ObserverActivity => {
      const segments = versions.map((v) => ({
        entityId: v.entityId,
        start: parseTime(v.content.startAt),
        end: parseTime(v.content.endAt),
        recordedAt: Math.min(parseTime(v.recordedAt) ?? now, now),
      }));
      writes.sort((a, b) => a.at - b.at);
      return {
        key: identity.key,
        entityId: identity.entityId,
        name: names.get(identity.key) ?? identity.key,
        category: identity.category,
        writes: writes.map((w) => w.at),
        seen: writes.map((w) => w.seenAt),
        lastSeenAt: Math.max(0, ...writes.map((w) => w.seenAt)),
        lastActivityAt: Math.max(0, writes.at(-1)?.at ?? 0, ...segments.map((s) => s.recordedAt)),
        observationCount: versions.length,
        segments,
      };
    });

    this.cached = { revision: this.revision, observers, observationCount: this.today.size };
    return this.cached;
  }

  private record(versions: Version<Observation>[], now: number): void {
    const writes = detectWrites(this.known, versions, now, !this.seeded);
    if (writes.length) this.writes = pruneWrites([...this.writes, ...writes], now).sort((a, b) => a.at - b.at);
    for (const version of versions) this.ensureIdentity(version.content.observerVersionId);
    this.changed();
  }

  private ensureIdentity(versionId: string): void {
    if (this.identities.has(versionId) || this.pending.has(versionId)) return;
    this.pending.add(versionId);
    this.resolve(versionId)
      .then((identity) => {
        this.identities.set(versionId, identity);
        this.changed();
      })
      .catch(() => {
        // 网络错误时留到下一次轮询再试。
      })
      .finally(() => this.pending.delete(versionId));
  }

  private changed(): void {
    this.revision += 1;
    for (const listener of this.listeners) listener();
  }
}
