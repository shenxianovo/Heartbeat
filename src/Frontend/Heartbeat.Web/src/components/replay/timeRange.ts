export interface TimeRange {
  start: number;
  end: number;
}
export type RangeDrag = "select" | "move" | "start" | "end";

export function clampRange(range: TimeRange, bounds: TimeRange): TimeRange {
  const span = Math.min(bounds.end - bounds.start, Math.max(1000, range.end - range.start));
  const start = Math.round(Math.max(bounds.start, Math.min(range.start, bounds.end - span)));
  return { start, end: Math.min(bounds.end, Math.round(start + span)) };
}

export function zoomRange(
  range: TimeRange,
  bounds: TimeRange,
  factor: number,
  pivot = 0.5,
): TimeRange {
  const at = range.start + (range.end - range.start) * pivot;
  const span = Math.min(
    bounds.end - bounds.start,
    Math.max(1000, (range.end - range.start) * factor),
  );
  return clampRange({ start: at - span * pivot, end: at + span * (1 - pivot) }, bounds);
}

export function dragRange(
  mode: RangeDrag,
  range: TimeRange,
  bounds: TimeRange,
  anchor: number,
  at: number,
): TimeRange {
  if (mode === "move")
    return clampRange({ start: range.start + at - anchor, end: range.end + at - anchor }, bounds);
  if (mode === "start")
    return {
      start: Math.round(Math.max(bounds.start, Math.min(at, range.end - 1000))),
      end: range.end,
    };
  if (mode === "end")
    return {
      start: range.start,
      end: Math.round(Math.min(bounds.end, Math.max(at, range.start + 1000))),
    };
  return clampRange({ start: Math.min(anchor, at), end: Math.max(anchor, at) }, bounds);
}

export function overlaps(start: number, end: number, range: TimeRange): boolean {
  return start === end
    ? start >= range.start && start < range.end
    : start < range.end && end > range.start;
}

export function percent(at: number, range: TimeRange): number {
  return Math.max(0, Math.min(100, ((at - range.start) / (range.end - range.start)) * 100));
}

/** A drag reads thousands of times, and building a formatter per reading dominates it. */
const timeFormats = new Map<boolean, Intl.DateTimeFormat>();

function timeFormat(seconds: boolean): Intl.DateTimeFormat {
  const cached = timeFormats.get(seconds);
  if (cached) return cached;
  const format = new Intl.DateTimeFormat("zh-CN", {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    ...(seconds ? { second: "2-digit" } : {}),
  });
  timeFormats.set(seconds, format);
  return format;
}

export function formatTime(at: number, seconds = false): string {
  return timeFormat(seconds).format(at);
}

export function timeTicks(range: TimeRange, count = 6) {
  const steps = [
    1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000, 900000, 1800000, 3600000,
    7200000, 14400000, 21600000, 43200000, 86400000,
  ];
  const step = steps.find((value) => (range.end - range.start) / value <= count) ?? 86400000;
  const offset = new Date(range.start).getTimezoneOffset() * 60000;
  const ticks = [];
  for (
    let at = Math.ceil((range.start - offset) / step) * step + offset;
    at <= range.end;
    at += step
  ) {
    ticks.push({ at, left: percent(at, range), label: formatTime(at, step < 60000) });
  }
  return ticks;
}

export function densityBucketSeconds(range: TimeRange): number {
  return (
    [1, 5, 15, 30, 60, 300, 900, 1800, 3600, 21600, 86400].find(
      (seconds) => (range.end - range.start) / 1000 / seconds <= 120,
    ) ?? 86400
  );
}
