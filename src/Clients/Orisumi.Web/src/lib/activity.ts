import type { Observation, ObservationPreview, Version } from "@/api/types";
import { nameObservers, type ObserverIdentity } from "./observers";
import { MINUTE, parseTime } from "./time";

/** 最近 60 秒内保存过 Observation 或更新过预览的 Observer 算正在写入。 */
export const ACTIVE_WINDOW = MINUTE;
/** 时间轴末尾放大的一段。 */
export const LENS = 10 * MINUTE;

/** 一行按哪种时间画：写有事实时间的 Observer 按事实时间，只写事实时间未知的陈述的按保存时间。 */
export type TimeBasis = "fact" | "recorded";

export interface Span {
  start: number;
  end: number;
}

/** 一份预览的事实边界。只知道一个边界时 start 和 end 相同。 */
export interface PreviewSpan extends Span {
  entityId: string;
  contentVersionId: string;
  updatedAt: number;
}

export interface ObserverRow {
  identity: ObserverIdentity;
  key: string;
  name: string;
  basis: TimeBasis;
  /** 按事实时间的时段。 */
  spans: Span[];
  /** 按事实时间的时间点，或者按保存时间的每份 Observation。 */
  ticks: number[];
  preview: PreviewSpan | null;
  /** 今天的 Observation 数。 */
  count: number;
  /** 最近一次保存 Observation 或更新预览的时刻，今天没有写入时为 0。 */
  lastAt: number;
}

export interface ActivityInput {
  /** 列出的全部 Observer 实体。 */
  observers: ObserverIdentity[];
  timedTypes: ReadonlySet<string>;
  /** Observer 版本解析出的 Observer。还没解析的返回 undefined，这些 Observation 暂不计入。 */
  identityOf: (observerVersionId: string) => ObserverIdentity | undefined;
  /** 事实时间在今天的 Observation。 */
  timed: Version<Observation>[];
  /** 只写事实时间未知的陈述的 Observer 今天保存的 Observation。 */
  recorded: Version<Observation>[];
  previews: ObservationPreview[];
  now: number;
}

/** 合并重叠或间隔不超过 gap 的时段，画出来是连续的一段。 */
export function union(spans: Span[], gap = 0): Span[] {
  const sorted = [...spans].sort((a, b) => a.start - b.start);
  const merged: Span[] = [];
  for (const span of sorted) {
    const last = merged.at(-1);
    if (last && span.start - last.end <= gap) last.end = Math.max(last.end, span.end);
    else merged.push({ ...span });
  }
  return merged;
}

export function isActive(row: ObserverRow, now: number): boolean {
  return row.lastAt > 0 && now - row.lastAt < ACTIVE_WINDOW;
}

function basisOf(identity: ObserverIdentity, timedTypes: ReadonlySet<string>): TimeBasis {
  if (!identity.entityId) return "fact";
  return identity.resourceName && timedTypes.has(identity.resourceName) ? "fact" : "recorded";
}

/** 每个 Observer 一行。正在写入的排在前面，其余按最近一次写入排列。 */
export function observerRows(input: ActivityInput): ObserverRow[] {
  const { now } = input;
  const rows = new Map<string, Omit<ObserverRow, "name">>();
  const row = (identity: ObserverIdentity) => {
    let entry = rows.get(identity.key);
    if (!entry) {
      entry = {
        identity,
        key: identity.key,
        basis: basisOf(identity, input.timedTypes),
        spans: [],
        ticks: [],
        preview: null,
        count: 0,
        lastAt: 0,
      };
      rows.set(identity.key, entry);
    }
    return entry;
  };
  const saved = (entry: Omit<ObserverRow, "name">, observation: Version<Observation>) => {
    entry.count += 1;
    entry.lastAt = Math.max(entry.lastAt, Math.min(parseTime(observation.recordedAt) ?? now, now));
  };

  for (const identity of input.observers) row(identity);

  for (const observation of input.timed) {
    const identity = input.identityOf(observation.content.observerVersionId);
    if (!identity) continue;
    const entry = row(identity);
    // 按保存时间画的 Observer，Observation 全部从引用方读出。
    if (entry.basis !== "fact") continue;
    saved(entry, observation);
    const start = parseTime(observation.content.startAt);
    const end = parseTime(observation.content.endAt);
    if (start !== null && (end === null || end > start)) entry.spans.push({ start, end: end ?? now });
    else if (start !== null || end !== null) entry.ticks.push((start ?? end)!);
  }

  for (const observation of input.recorded) {
    const identity = input.identityOf(observation.content.observerVersionId);
    if (!identity) continue;
    const entry = row(identity);
    if (entry.basis !== "recorded") continue;
    saved(entry, observation);
    entry.ticks.push(Math.min(parseTime(observation.recordedAt) ?? now, now));
  }

  for (const preview of input.previews) {
    const identity = input.identityOf(preview.content.observerVersionId);
    if (!identity) continue;
    const entry = row(identity);
    const updatedAt = Math.min(parseTime(preview.updatedAt) ?? now, now);
    if (entry.preview && entry.preview.updatedAt >= updatedAt) continue;
    const start = parseTime(preview.content.startAt);
    const end = parseTime(preview.content.endAt);
    const from = start ?? end ?? updatedAt;
    entry.preview = {
      entityId: preview.entityId,
      contentVersionId: preview.content.contentVersionId,
      start: from,
      end: Math.max(from, end ?? (start !== null ? updatedAt : from)),
      updatedAt,
    };
    entry.lastAt = Math.max(entry.lastAt, updatedAt);
  }

  const list = [...rows.values()];
  const names = nameObservers(list.map((r) => r.identity));
  return list
    .map((r) => ({ ...r, name: names.get(r.key) ?? r.key }))
    .sort(
      (a, b) =>
        Number(isActive(b, now)) - Number(isActive(a, now)) ||
        b.lastAt - a.lastAt ||
        a.name.localeCompare(b.name, "zh") ||
        a.key.localeCompare(b.key),
    );
}
