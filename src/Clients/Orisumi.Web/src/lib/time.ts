export const SECOND = 1000;
export const MINUTE = 60 * SECOND;
export const HOUR = 60 * MINUTE;
export const DAY = 24 * HOUR;

export interface Range {
  from: string;
  to: string;
}

/** 浏览器时区的今天 [00:00, 次日 00:00)。 */
export function dayRange(now: number): Range & { key: string; start: number; end: number } {
  const date = new Date(now);
  const start = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
  const end = new Date(date.getFullYear(), date.getMonth(), date.getDate() + 1).getTime();
  const key = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  return { key, start, end, from: new Date(start).toISOString(), to: new Date(end).toISOString() };
}

/** “现在”轮询的范围 [now - 2 分钟, now + 1 分钟)。 */
export function liveRange(now: number): Range {
  return { from: new Date(now - 2 * MINUTE).toISOString(), to: new Date(now + MINUTE).toISOString() };
}

export function parseTime(value: string | null | undefined): number | null {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isNaN(time) ? null : time;
}

export function ago(ms: number): string {
  if (ms < SECOND) return "刚刚";
  if (ms < MINUTE) return `${Math.round(ms / SECOND)} 秒前`;
  if (ms < HOUR) return `${Math.round(ms / MINUTE)} 分钟前`;
  if (ms < DAY) return `${Math.round(ms / HOUR)} 小时前`;
  return `${Math.round(ms / DAY)} 天前`;
}

export function pad(value: number): string {
  return value < 10 ? `0${value}` : String(value);
}

export function clock(ms: number, seconds = false): string {
  const date = new Date(ms);
  const base = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  return seconds ? `${base}:${pad(date.getSeconds())}` : base;
}

export function dateTime(ms: number): string {
  const date = new Date(ms);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${clock(ms, true)}`;
}
