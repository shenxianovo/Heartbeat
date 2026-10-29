import type { ReactNode } from "react";
import { hourly, isActive, type ObserverActivity } from "@/lib/activity";
import { clock, HOUR, MINUTE } from "@/lib/time";

const WEEKDAYS = ["日", "一", "二", "三", "四", "五", "六"];

/** “现在”的开头：正在写入的 Observer、时钟、今天的数字和今天每小时的节奏。 */
export function NowSummary({
  now,
  dayStart,
  observers,
  observationCount,
  loading,
  selected,
}: {
  now: number;
  dayStart: number;
  observers: ObserverActivity[];
  observationCount: number;
  loading: boolean;
  selected: ObserverActivity | null;
}) {
  const active = observers.filter((o) => isActive(o, now)).length;
  const perMinute = observers.reduce((sum, o) => sum + o.writes.filter((at) => now - at < MINUTE).length, 0);
  const date = new Date(now);

  return (
    <section aria-label="现在的状态" className="mb-4">
      <p aria-live="polite" className="mb-3">
        {loading ? "正在读取今天的数据" : `${observers.length} 个 Observer，${active} 个正在写入`}
      </p>
      <div className="flex flex-wrap items-end gap-x-8 gap-y-4">
        <dl className="flex flex-wrap items-end gap-x-8 gap-y-2">
          <Stat label={`${date.getMonth() + 1} 月 ${date.getDate()} 日 · 周${WEEKDAYS[date.getDay()]}`}>
            <span className="text-[26px] font-medium">{clock(now)}</span>
          </Stat>
          <Stat label="今天的 Observation">{loading ? "…" : observationCount.toLocaleString()}</Stat>
          <Stat label="最近一分钟的写入">{loading ? "…" : perMinute.toLocaleString()}</Stat>
        </dl>
        <DayRhythm
          hours={hourly(selected ? [selected] : observers, dayStart)}
          current={Math.floor((now - dayStart) / HOUR)}
        />
      </div>
    </section>
  );
}

/** 今天 24 小时的节奏：每小时有多少份 Observation，按事实时间。还没到的小时留一道浅痕。 */
function DayRhythm({ hours, current }: { hours: number[]; current: number }) {
  // 开根号，免得最忙的一小时把其他小时压平。
  const max = Math.sqrt(Math.max(1, ...hours));
  return (
    <figure className="min-w-[200px] flex-1">
      <figcaption className="text-fg-3 mb-1.5 flex justify-between text-xs">
        <span>今天的节奏</span>
        <span>按事实时间</span>
      </figcaption>
      <div role="img" aria-label="今天每小时的记录数" className="flex h-9 items-end gap-[3px]">
        {hours.map((count, hour) => (
          <span
            key={hour}
            title={`${hour}:00 · ${count} 份`}
            className={
              hour > current
                ? "bg-line flex-1 rounded-sm"
                : hour === current
                  ? "bg-fg flex-1 rounded-sm"
                  : "bg-fg-3 flex-1 rounded-sm"
            }
            style={{ height: hour > current || !count ? 2 : `${Math.max(10, (Math.sqrt(count) / max) * 100)}%` }}
          />
        ))}
      </div>
      <div aria-hidden="true" className="text-fg-3 mt-1 grid grid-cols-4 text-[11px] tabular-nums">
        <span>0</span>
        <span>6</span>
        <span>12</span>
        <span>18</span>
      </div>
    </figure>
  );
}

function Stat({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col-reverse">
      <dt className="text-fg-3 mt-1 text-xs">{label}</dt>
      <dd className="text-[20px] leading-none tabular-nums">{children}</dd>
    </div>
  );
}
