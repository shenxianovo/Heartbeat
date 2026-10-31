import { isActive, LENS, union, type ObserverRow, type Span } from "@/lib/activity";
import { categoryInfo } from "@/lib/categories";
import { ago } from "@/lib/time";
import { cn } from "@/lib/utils";
import { Mark } from "../token/Token";
import { useHighlight, useStack } from "../token/context";

const GRID = "grid grid-cols-[minmax(0,1fr)_auto] gap-x-4 gap-y-2 md:grid-cols-[184px_minmax(0,1fr)_136px]";
/** 时间轴：今天占 4/5，最近 10 分钟占 1/5，中间留一道缝。 */
const LANE = "grid grid-cols-[minmax(0,4fr)_minmax(0,1fr)] gap-1.5";

/** 每个 Observer 一行，时间轴对齐：左边是今天，右边是放大的最近 10 分钟。 */
export function ObserverRows({
  rows,
  day,
  now,
}: {
  rows: ObserverRow[];
  day: { start: number; end: number };
  now: number;
}) {
  return (
    <div role="list" aria-label="Observer">
      <div aria-hidden="true" className={cn(GRID, "text-fg-3 pb-1.5 text-[11px] tabular-nums max-md:hidden")}>
        <span />
        <div className={LANE}>
          <div className="flex justify-between">
            {[0, 6, 12, 18, 24].map((hour) => (
              <span key={hour}>{hour}</span>
            ))}
          </div>
          <div className="flex justify-between">
            <span>10 分钟前</span>
            <span>现在</span>
          </div>
        </div>
        <span />
      </div>
      {rows.map((row) => (
        <Row key={row.key} row={row} day={day} now={now} />
      ))}
    </div>
  );
}

function Row({ row, day, now }: { row: ObserverRow; day: { start: number; end: number }; now: number }) {
  const { ids, open } = useStack();
  const highlight = useHighlight();
  const category = categoryInfo(row.identity.category);
  const active = isActive(row, now);
  const entityId = row.identity.entityId;
  const opened = !!entityId && ids.at(-1) === entityId;
  const lit = !!entityId && highlight.active === entityId;

  return (
    <div role="listitem">
      <button
        type="button"
        disabled={!entityId}
        aria-pressed={entityId ? opened : undefined}
        onClick={() => entityId && open(entityId, -1)}
        onPointerEnter={() => entityId && highlight.set(entityId)}
        onPointerLeave={() => entityId && highlight.set(null)}
        className={cn(
          GRID,
          "border-line -mx-2 w-[calc(100%+16px)] items-center rounded-lg border-t px-2 py-3 text-left transition-colors",
          entityId ? "hover:bg-panel cursor-pointer" : "cursor-default",
          (opened || lit) && "bg-panel",
        )}
        style={
          active && !opened && !lit
            ? { background: `color-mix(in srgb, ${category.color} 9%, transparent)` }
            : undefined
        }
      >
        <span className="flex min-w-0 items-center gap-2.5">
          <Mark role={entityId ? "source" : null} color={null} size="sm" />
          <span className="min-w-0">
            <span className="block truncate text-[13px]">{row.name}</span>
            <span className="text-fg-3 block truncate text-xs">
              {category.label} · {row.basis === "fact" ? "按事实时间" : "按保存时间"}
            </span>
          </span>
        </span>
        <span className={cn(LANE, "col-span-2 h-7 max-md:order-last md:order-none md:col-span-1")}>
          <Lane row={row} from={day.start} to={day.end} now={now} color={category.color} />
          <Lane row={row} from={now - LENS} to={now} now={now} color={category.color} lens active={active} />
        </span>
        <Status row={row} now={now} />
      </button>
    </div>
  );
}

/** 一段时间轴。横向按时间拉伸，竖线和横条的高度不变。 */
function Lane({
  row,
  from,
  to,
  now,
  color,
  lens,
  active,
}: {
  row: ObserverRow;
  from: number;
  to: number;
  now: number;
  color: string;
  lens?: boolean;
  active?: boolean;
}) {
  const width = to - from;
  const x = (at: number) => ((Math.min(Math.max(at, from), to) - from) / width) * 1000;
  const visible = (span: Span) => span.end >= from && span.start <= to;
  const tick = lens ? 3 : 2;

  return (
    <span className="bg-line relative block h-7 overflow-hidden rounded">
      <svg viewBox="0 0 1000 28" preserveAspectRatio="none" aria-hidden="true" className="absolute inset-0 size-full">
        {union(row.spans.filter(visible), width / 1000).map((span, i) => (
          <rect
            key={`s${i}`}
            x={x(span.start)}
            y={9}
            width={Math.max(1.5, x(span.end) - x(span.start))}
            height={10}
            fill={color}
            opacity={0.75}
          />
        ))}
        {row.ticks
          .filter((at) => at >= from && at <= to)
          .map((at, i) => (
            <rect key={`t${i}`} x={x(at) - tick / 2} y={5} width={tick} height={18} fill={color} opacity={0.85} />
          ))}
        {!lens && now > from && now < to && <rect x={x(now) - 1} y={0} width={2} height={28} fill="var(--fg-3)" />}
      </svg>
      {lens && active && (
        <span
          aria-hidden="true"
          className="beat absolute top-1/2 right-1 size-2.5 -translate-y-1/2 rounded-full shadow-[0_0_0_2px_var(--card)]"
          style={{ background: color }}
        />
      )}
    </span>
  );
}

function Status({ row, now }: { row: ObserverRow; now: number }) {
  return (
    <span className="min-w-0 text-right">
      <span className={cn("block text-[13px] tabular-nums", !row.lastAt && "text-fg-3")}>
        {row.lastAt ? ago(now - row.lastAt) : "今天没有写入"}
      </span>
      <span className="text-fg-3 block text-xs tabular-nums">今天 {row.count.toLocaleString()} 份</span>
    </span>
  );
}
