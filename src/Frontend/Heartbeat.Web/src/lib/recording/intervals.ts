import type { TimelineRecord } from "@/api/types";

export interface ObservationInterval {
  from: number;
  to: number;
}

/** Clips an explicit observation to [from, to), without extending or changing the Record. */
export function clipExplicitRange(
  record: Pick<TimelineRecord, "startedAt" | "endedAt">,
  from: number,
  to: number,
): ObservationInterval | null {
  const start = Date.parse(record.startedAt);
  const end = Date.parse(record.endedAt ?? "");
  if (![start, end, from, to].every(Number.isFinite) || end < start || from >= to) return null;
  if (start === end) return start >= from && start < to ? { from: start, to: end } : null;
  if (start >= to || end <= from) return null;
  return { from: Math.max(from, start), to: Math.min(to, end) };
}

/** Union duration of valid intervals. Callers choose which observations belong together. */
export function coveredMilliseconds(intervals: readonly ObservationInterval[]): number {
  let end = -Infinity;
  let milliseconds = 0;
  for (const interval of [...intervals].sort((a, b) => a.from - b.from)) {
    milliseconds += Math.max(0, interval.to - Math.max(interval.from, end));
    end = Math.max(end, interval.to);
  }
  return milliseconds;
}
