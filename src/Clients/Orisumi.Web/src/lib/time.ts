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

/** “1 小时 12 分”“18 分钟”“40 秒”。 */
export function duration(ms: number): string {
  if (ms < MINUTE) return `${Math.max(0, Math.round(ms / SECOND))} 秒`;
  const minutes = Math.round(ms / MINUTE);
  if (minutes < 60) return `${minutes} 分钟`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `${hours} 小时 ${rest} 分` : `${hours} 小时`;
}

/** “2026-10-29” → 那一天的范围；格式不对时返回 null。 */
export function dayOf(key: string): ReturnType<typeof dayRange> | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(key);
  if (!match) return null;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 12);
  return Number.isNaN(date.getTime()) ? null : dayRange(date.getTime());
}

/** “10 月 29 日 星期四”。 */
export function dayTitle(ms: number): string {
  const date = new Date(ms);
  return `${date.getMonth() + 1} 月 ${date.getDate()} 日 星期${"日一二三四五六"[date.getDay()]}`;
}
