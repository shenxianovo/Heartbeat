/**
 * Heartbeat 时间展示工具。
 * 后端统一返回以 Z 结尾的 UTC 字符串，截断到整微秒；前端按浏览器时区渲染。
 * 未知（null）显式显示为「未知」，绝不拿观测时间或当前时间填补。
 */

const dateTimeFmt = new Intl.DateTimeFormat("zh-CN", {
  dateStyle: "medium",
  timeStyle: "long",
  hour12: false,
});

const longDateFmt = new Intl.DateTimeFormat("zh-CN", { dateStyle: "long", hour12: false });

export function formatDateTime(value: string | null | undefined): string {
  if (value == null) return "未知";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? "无效时间" : dateTimeFmt.format(d);
}

export function formatDate(value: string | null | undefined): string {
  if (value == null) return "未知";
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? "无效时间" : longDateFmt.format(d);
}

export function formatDuration(start: string, end: string | null): string {
  if (end == null) return "进行中 · 结束未知";
  const ms = new Date(end).getTime() - new Date(start).getTime();
  if (!Number.isFinite(ms) || ms < 0) return "—";
  if (ms === 0) return "时间点";
  const sec = Math.round(ms / 1000);
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  const parts = [h > 0 ? `${h} 小时` : null, m > 0 ? `${m} 分` : null, h === 0 && s > 0 ? `${s} 秒` : null].filter(
    Boolean,
  );
  return parts.length > 0 ? parts.join(" ") : `${ms} 毫秒`;
}
