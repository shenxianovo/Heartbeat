export interface DateRange {
  from: string;
  to: string;
}

function pad(value: number): string {
  return String(value).padStart(2, "0");
}

export function toLocalInputValue(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export function todayRange(now = new Date()): DateRange {
  const from = new Date(now);
  from.setHours(0, 0, 0, 0);
  const to = new Date(from);
  to.setDate(to.getDate() + 1);
  return { from: toLocalInputValue(from), to: toLocalInputValue(to) };
}

export function isWholeDay(range: DateRange): boolean {
  const day = todayRange(new Date(range.from));
  return range.from === day.from && range.to === day.to;
}

/**
 * Moves a whole day by one day. A custom window keeps its span and local clock
 * times, moving by its own length in whole days (at least one).
 */
export function stepRange(range: DateRange, step: number): DateRange {
  const span = Date.parse(range.to) - Date.parse(range.from);
  const days = isWholeDay(range) ? step : step * Math.max(1, Math.round(span / 86_400_000));
  const shift = (value: string) => {
    const date = new Date(value);
    date.setDate(date.getDate() + days);
    return toLocalInputValue(date);
  };
  return { from: shift(range.from), to: shift(range.to) };
}

export function last24HoursRange(now = new Date()): DateRange {
  const from = new Date(now.getTime() - 24 * 60 * 60 * 1000);
  return { from: toLocalInputValue(from), to: toLocalInputValue(now) };
}

export function rangeToIso(range: DateRange): DateRange | null {
  const from = new Date(range.from);
  const to = new Date(range.to);
  if (!Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || from >= to) {
    return null;
  }
  return { from: from.toISOString(), to: to.toISOString() };
}

const dateTimeFormatter = new Intl.DateTimeFormat("zh-CN", {
  year: "numeric",
  month: "short",
  day: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
});

export function formatDateTime(value: string): string {
  return dateTimeFormatter.format(new Date(value));
}

export function formatDuration(startedAt: string, endedAt: string | null): string | null {
  if (!endedAt) return null;
  const milliseconds = Math.max(0, new Date(endedAt).getTime() - new Date(startedAt).getTime());
  const seconds = Math.round(milliseconds / 1000);
  if (seconds < 60) return `${seconds} 秒`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  if (minutes < 60) return rest ? `${minutes} 分 ${rest} 秒` : `${minutes} 分钟`;
  const hours = Math.floor(minutes / 60);
  const restMinutes = minutes % 60;
  return restMinutes ? `${hours} 小时 ${restMinutes} 分` : `${hours} 小时`;
}

export function formatDurationMinutes(milliseconds: number): string {
  const minutes = Math.floor(milliseconds / 60000);
  if (minutes < 1) return "不足 1 分钟";
  return minutes < 60 ? `${minutes} 分钟` : `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分`;
}
