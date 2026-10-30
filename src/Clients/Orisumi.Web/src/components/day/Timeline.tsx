import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import type { DaySummary, Row, Segment } from "@/lib/day";
import { ticks as makeTicks, tickStep } from "@/lib/day";
import { clock, duration, HOUR, MINUTE } from "@/lib/time";
import { cn } from "@/lib/utils";
import { useHighlight, useStack } from "../token/context";
import { Token } from "../token/Token";

const LABEL = 272;
const ROW = 30;
const LANE = 20;
const AXIS = 22;
const MIN_SPAN = 5 * MINUTE;
const VISIBLE_THINGS = 12;

type View = [number, number];

/**
 * 一天的时间线：上面是全天的缩略条，拖动或拉两端选范围；下面每个事物一行，不引用事物的经历按类型一行，
 * 只有时间点的 Observation 画成密度。捏合或 ⌘ + 滚轮缩放，拖动平移；预设范围之间平滑过渡。
 * 事物的行左边就是排行：名次、记号和时长，底色的长度是它占最多者的比例，和右边它的段在同一行。
 */
export function Timeline({
  summary,
  from,
  to,
  now,
  today,
  details,
}: {
  summary: DaySummary;
  from: number;
  to: number;
  now: number;
  today: boolean;
  /** 同名事物后面的补充，按 EntityId。 */
  details: ReadonlyMap<string, string | null>;
}) {
  const extent = useMemo<View>(() => {
    const all = [...summary.things, ...summary.lanes].flatMap((row) => row.segments);
    if (!all.length) return [from, to];
    const first = Math.min(...all.map((s) => s.start));
    const last = Math.max(...all.map((s) => s.end), today ? now : 0);
    return [Math.max(from, first - 20 * MINUTE), Math.min(to, last + 20 * MINUTE)];
    // 范围只在换一天时重新决定，之后由用户控制。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [from, to]);
  const [view, setView] = useState<View>(extent);
  const [shownExtent, setShownExtent] = useState(extent);
  if (shownExtent !== extent) {
    setShownExtent(extent);
    setView(extent);
  }
  const animate = useAnimatedView(view, setView);
  const [expanded, setExpanded] = useState(false);
  const [width, setWidth] = useState(0);
  const [box, setBox] = useState<HTMLDivElement | null>(null);
  useEffect(() => {
    if (!box) return;
    const observer = new ResizeObserver(([entry]) => setWidth(Math.round(entry!.contentRect.width)));
    observer.observe(box);
    return () => observer.disconnect();
  }, [box]);

  // 窄屏时排行收窄，给时间轴留出地方。
  const label = width < 720 ? 200 : LABEL;
  const plot = Math.max(0, width - label);
  const span = view[1] - view[0];
  const x = (t: number) => ((t - view[0]) / span) * plot;
  const things = expanded ? summary.things : summary.things.slice(0, VISIBLE_THINGS);
  const hidden = summary.things.length - things.length;
  const longest = Math.max(1, ...summary.things.map((row) => row.total));
  const highlight = useHighlight();
  const stack = useStack();

  const clampView = useCallback(
    ([a, b]: View): View => {
      let s = Math.max(MIN_SPAN, Math.min(to - from, b - a));
      let start = Math.min(Math.max(a, from), to - s);
      if (b - a < MIN_SPAN) start = Math.min(Math.max((a + b) / 2 - s / 2, from), to - s);
      s = Math.min(s, to - start);
      return [start, start + s];
    },
    [from, to],
  );

  // 捏合（触控板发出带 ctrlKey 的 wheel）或 ⌘ + 滚轮缩放，横向滚动平移。
  const plotRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = plotRef.current;
    if (!element) return;
    function onWheel(event: WheelEvent) {
      const rect = element!.getBoundingClientRect();
      const at = view[0] + ((event.clientX - rect.left) / rect.width) * span;
      if (event.ctrlKey || event.metaKey) {
        event.preventDefault();
        const factor = Math.exp(event.deltaY * 0.01);
        setView(clampView([at - (at - view[0]) * factor, at + (view[1] - at) * factor]));
      } else if (Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
        event.preventDefault();
        const shift = (event.deltaX / rect.width) * span;
        setView(clampView([view[0] + shift, view[1] + shift]));
      }
    }
    element.addEventListener("wheel", onWheel, { passive: false });
    return () => element.removeEventListener("wheel", onWheel);
  }, [view, span, clampView]);

  const drag = useRef<{ x: number; view: View } | null>(null);
  function onPlotDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || (event.target as Element).closest("[data-segment]")) return;
    drag.current = { x: event.clientX, view };
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function onPlotMove(event: ReactPointerEvent<HTMLDivElement>) {
    if (!drag.current || !plot) return;
    const shift = ((drag.current.x - event.clientX) / plot) * span;
    setView(clampView([drag.current.view[0] + shift, drag.current.view[1] + shift]));
  }

  const step = tickStep(span, Math.max(3, Math.floor(plot / 90)));
  const marks = makeTicks(view[0], view[1], step);
  const nowX = today && now >= view[0] && now <= view[1] ? x(now) : null;

  const presets: { label: string; view: View }[] = [
    { label: "全天", view: [from, to] },
    { label: "有记录的时段", view: extent },
    ...(today ? [{ label: "最近 1 小时", view: [now - HOUR + 5 * MINUTE, now + 5 * MINUTE] as View }] : []),
  ];

  return (
    <div ref={setBox} className="min-w-0 select-none">
      <div className="mb-3 flex flex-wrap items-center gap-1">
        {presets.map((preset) => {
          const on = Math.abs(preset.view[0] - view[0]) < MINUTE && Math.abs(preset.view[1] - view[1]) < MINUTE;
          return (
            <button
              key={preset.label}
              type="button"
              aria-pressed={on}
              onClick={() => animate(clampView(preset.view))}
              className={cn(
                "cursor-pointer rounded-md px-2.5 py-1 text-xs transition-colors",
                on ? "bg-panel text-fg font-medium" : "text-fg-2 hover:text-fg hover:bg-panel",
              )}
            >
              {preset.label}
            </button>
          );
        })}
        <span className="flex-1" />
        <span className="text-fg-3 text-xs tabular-nums" title="捏合或 ⌘ + 滚轮缩放，拖动平移">
          {clock(view[0])} – {clock(view[1])}
        </span>
      </div>

      {width > 0 && (
        <Minimap
          summary={summary}
          from={from}
          to={to}
          view={view}
          width={width}
          now={today ? now : null}
          onChange={(next) => setView(clampView(next))}
        />
      )}

      <div className="mt-3 flex">
        <div className="shrink-0 pr-4" style={{ width: label }}>
          <div className="text-fg-3 flex items-end justify-between px-2 pb-1 text-[11px]" style={{ height: AXIS }}>
            <span>排行</span>
            <span>时长</span>
          </div>
          <ol aria-label="排行">
            {things.map((row, i) => {
              const lit = highlight.active === row.key;
              return (
                <li key={row.key} style={{ height: ROW }} className="py-[2px]">
                  <div
                    className={cn(
                      "relative grid h-full cursor-pointer grid-cols-[1.25rem_minmax(0,1fr)_auto] items-center gap-x-1 overflow-hidden rounded-md px-2 transition-colors duration-150",
                      lit ? "bg-accent-soft" : "hover:bg-panel/70",
                    )}
                    onPointerEnter={() => highlight.set(row.key)}
                    onPointerLeave={() => highlight.set(null)}
                    onClick={() => stack.open(row.key, -1)}
                  >
                    <span
                      aria-hidden="true"
                      className="absolute inset-y-0 left-0 rounded-md transition-[width] duration-300 ease-out"
                      style={{
                        width: `${(row.total / longest) * 100}%`,
                        background: `color-mix(in srgb, ${row.color ?? "var(--fg-3)"} 11%, transparent)`,
                      }}
                    />
                    <span className="text-fg-3 relative text-[11px] tabular-nums">{i + 1}</span>
                    <span className="relative min-w-0">
                      <Token version={row.thing!} size="sm" detail={details.get(row.key)} />
                    </span>
                    <span className="text-fg-2 relative text-xs tabular-nums">{duration(row.total)}</span>
                  </div>
                </li>
              );
            })}
          </ol>
          {hidden > 0 || expanded ? (
            <div className="flex items-center" style={{ height: ROW }}>
              <button
                type="button"
                onClick={() => setExpanded((value) => !value)}
                className="text-fg-2 hover:text-fg hover:bg-panel cursor-pointer rounded-md px-2 py-0.5 text-xs"
              >
                {expanded ? "收起" : `还有 ${hidden} 个`}
              </button>
            </div>
          ) : null}
          {summary.lanes.length + summary.points.length > 0 && <div className="border-line mt-1 border-t pt-1" />}
          {summary.lanes.map((row) => (
            <div key={row.key} className="text-fg-2 flex items-center truncate px-2 text-xs" style={{ height: LANE }}>
              {row.title}
            </div>
          ))}
          {summary.points.map((group) => (
            <div key={group.key} className="text-fg-3 flex items-center truncate px-2 text-xs" style={{ height: LANE }}>
              {group.title}
            </div>
          ))}
        </div>

        <div
          ref={plotRef}
          className="relative min-w-0 flex-1 cursor-grab touch-pan-y overflow-hidden active:cursor-grabbing"
          onPointerDown={onPlotDown}
          onPointerMove={onPlotMove}
          onPointerUp={() => (drag.current = null)}
          onPointerCancel={() => (drag.current = null)}
        >
          {plot > 0 && (
            <Plot
              summary={summary}
              things={things}
              expandedRow={hidden > 0 || expanded}
              plot={plot}
              x={x}
              marks={marks}
              step={step}
              nowX={nowX}
              view={view}
            />
          )}
        </div>
      </div>
    </div>
  );
}

function Plot({
  summary,
  things,
  expandedRow,
  plot,
  x,
  marks,
  step,
  nowX,
  view,
}: {
  summary: DaySummary;
  things: Row[];
  expandedRow: boolean;
  plot: number;
  x: (t: number) => number;
  marks: number[];
  step: number;
  nowX: number | null;
  view: View;
}) {
  const highlight = useHighlight();
  const stack = useStack();
  const [hover, setHover] = useState<{ segment: Segment; row: Row; left: number; top: number } | null>(null);
  const extra = (summary.lanes.length + summary.points.length > 0 ? 9 : 0) + (expandedRow ? ROW : 0);
  const height = AXIS + things.length * ROW + extra + (summary.lanes.length + summary.points.length) * LANE;
  const active = highlight.active;
  const lanesTop = AXIS + things.length * ROW + extra;

  return (
    <>
      <svg width={plot} height={height} className="block" role="img" aria-label="这一天的时间线">
        {marks.map((t) => (
          <g key={t}>
            <line x1={x(t)} x2={x(t)} y1={AXIS - 4} y2={height} stroke="var(--line)" />
            <text x={x(t) + 3} y={12} style={{ fontSize: 10, fill: "var(--fg-3)" }}>
              {step < HOUR ? clock(t) : `${new Date(t).getHours()}:00`}
            </text>
          </g>
        ))}
        {things.map((row, i) => {
          const y = AXIS + i * ROW;
          const dim = active !== null && active !== row.key;
          return (
            <g key={row.key} opacity={dim ? 0.18 : 1} style={{ transition: "opacity 150ms ease-out" }}>
              <rect x={0} y={y + ROW / 2 - 0.5} width={plot} height={1} fill="var(--line)" opacity={0.6} />
              {row.segments.map((segment) => {
                if (segment.end < view[0] || segment.start > view[1]) return null;
                const left = x(segment.start);
                const w = Math.max(1.5, x(segment.end) - left);
                return (
                  <rect
                    key={segment.observationId}
                    data-segment=""
                    x={left}
                    y={y + 7}
                    width={w}
                    height={ROW - 14}
                    rx={Math.min(3, w / 2)}
                    fill={row.color ?? "var(--fg-3)"}
                    className="cursor-pointer"
                    onPointerEnter={(event) => {
                      highlight.set(row.key);
                      const box = (event.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect();
                      setHover({ segment, row, left: event.clientX - box.left, top: y });
                    }}
                    onPointerLeave={() => {
                      highlight.set(null);
                      setHover(null);
                    }}
                    onClick={() => stack.open(segment.contentEntityId ?? segment.observationId, -1)}
                  />
                );
              })}
            </g>
          );
        })}
        {summary.lanes.map((row, i) => {
          const y = lanesTop + i * LANE;
          return (
            <g key={row.key}>
              {row.segments.map((segment) => {
                if (segment.end < view[0] || segment.start > view[1]) return null;
                const left = x(segment.start);
                return (
                  <rect
                    key={segment.observationId}
                    data-segment=""
                    x={left}
                    y={y + 6}
                    width={Math.max(1.5, x(segment.end) - left)}
                    height={LANE - 12}
                    rx={2}
                    fill="var(--fg-3)"
                    opacity={0.55}
                    className="cursor-pointer"
                    onPointerEnter={(event) => {
                      const box = (event.currentTarget.ownerSVGElement as SVGSVGElement).getBoundingClientRect();
                      setHover({ segment, row, left: event.clientX - box.left, top: y });
                    }}
                    onPointerLeave={() => setHover(null)}
                    onClick={() => stack.open(segment.contentEntityId ?? segment.observationId, -1)}
                  />
                );
              })}
            </g>
          );
        })}
        {summary.points.map((group, i) => (
          <Density
            key={group.key}
            times={group.times}
            y={lanesTop + (summary.lanes.length + i) * LANE}
            plot={plot}
            view={view}
          />
        ))}
        {nowX !== null && (
          <g>
            <line x1={nowX} x2={nowX} y1={AXIS - 6} y2={height} stroke="var(--accent)" strokeWidth={1.25} />
            <circle cx={nowX} cy={AXIS - 6} r={2.5} fill="var(--accent)" />
          </g>
        )}
      </svg>
      {hover && (
        <div
          role="tooltip"
          className="bg-card border-line shadow-float pointer-events-none absolute z-10 rounded-lg border px-2.5 py-1.5 text-xs"
          style={{
            left: Math.min(Math.max(0, hover.left - 80), plot - 180),
            top: hover.top + ROW + 2,
            width: 180,
          }}
        >
          <p className="text-fg truncate font-medium">{hover.segment.label}</p>
          <p className="text-fg-2 tabular-nums">
            {clock(hover.segment.start)} – {clock(hover.segment.end)} ·{" "}
            {duration(hover.segment.end - hover.segment.start)}
          </p>
        </div>
      )}
    </>
  );
}

/** 只有时间点的 Observation：按像素分桶，柱高随数量的平方根变化。 */
function Density({ times, y, plot, view }: { times: number[]; y: number; plot: number; view: View }) {
  const bins = Math.max(1, Math.floor(plot / 3));
  const counts = new Array<number>(bins).fill(0);
  const span = view[1] - view[0];
  for (const t of times) {
    if (t < view[0] || t >= view[1]) continue;
    counts[Math.floor(((t - view[0]) / span) * bins)]! += 1;
  }
  const max = Math.max(1, ...counts);
  return (
    <g>
      {counts.map((count, i) =>
        count ? (
          <rect
            key={i}
            x={(i / bins) * plot}
            width={2}
            y={y + LANE - 4 - Math.sqrt(count / max) * (LANE - 8)}
            height={Math.sqrt(count / max) * (LANE - 8)}
            fill="var(--fg-3)"
            opacity={0.5}
          />
        ) : null,
      )}
    </g>
  );
}

/** 全天的缩略条：所有事物的段画成灰色，选中的范围可以拖动，也可以拉两端。 */
function Minimap({
  summary,
  from,
  to,
  view,
  width,
  now,
  onChange,
}: {
  summary: DaySummary;
  from: number;
  to: number;
  view: View;
  width: number;
  now: number | null;
  onChange: (view: View) => void;
}) {
  const span = to - from;
  const x = (t: number) => ((t - from) / span) * width;
  const drag = useRef<{ mode: "move" | "start" | "end" | "new"; x: number; view: View } | null>(null);
  const highlight = useHighlight();

  function down(event: ReactPointerEvent<SVGElement>, mode: "move" | "start" | "end" | "new") {
    event.stopPropagation();
    const svg = (event.currentTarget as SVGElement).ownerSVGElement ?? (event.currentTarget as SVGSVGElement);
    svg.setPointerCapture(event.pointerId);
    const box = svg.getBoundingClientRect();
    const at = from + ((event.clientX - box.left) / box.width) * span;
    drag.current = { mode, x: event.clientX, view: mode === "new" ? [at, at] : view };
  }
  function move(event: ReactPointerEvent<SVGSVGElement>) {
    const state = drag.current;
    if (!state) return;
    const shift = ((event.clientX - state.x) / width) * span;
    const [a, b] = state.view;
    if (state.mode === "move") onChange([a + shift, b + shift]);
    else if (state.mode === "start") onChange([Math.min(a + shift, b - MIN_SPAN), b]);
    else if (state.mode === "end") onChange([a, Math.max(b + shift, a + MIN_SPAN)]);
    else onChange(shift >= 0 ? [a, a + shift] : [a + shift, a]);
  }

  return (
    <svg
      width={width}
      height={28}
      className="bg-panel block cursor-crosshair rounded-lg"
      onPointerDown={(event) => down(event, "new")}
      onPointerMove={move}
      onPointerUp={() => (drag.current = null)}
      aria-label="全天"
      role="img"
    >
      {summary.things.map((row) =>
        row.segments.map((segment) => (
          <rect
            key={segment.observationId + row.key}
            x={x(segment.start)}
            width={Math.max(1, x(segment.end) - x(segment.start))}
            y={8}
            height={12}
            fill={highlight.active === row.key ? (row.color ?? "var(--fg-3)") : "var(--fg-3)"}
            opacity={highlight.active === row.key ? 1 : 0.35}
          />
        )),
      )}
      {[6, 12, 18].map((h) => (
        <line key={h} x1={(h / 24) * width} x2={(h / 24) * width} y1={0} y2={4} stroke="var(--line-2)" />
      ))}
      {now !== null && <line x1={x(now)} x2={x(now)} y1={0} y2={28} stroke="var(--accent)" />}
      <g>
        <rect
          x={x(view[0])}
          y={1}
          width={Math.max(4, x(view[1]) - x(view[0]))}
          height={26}
          rx={4}
          fill="var(--accent-soft)"
          stroke="var(--accent)"
          strokeOpacity={0.55}
          className="cursor-grab"
          onPointerDown={(event) => down(event, "move")}
        />
        <rect
          x={x(view[0]) - 3}
          y={1}
          width={6}
          height={26}
          fill="transparent"
          className="cursor-ew-resize"
          onPointerDown={(event) => down(event, "start")}
        />
        <rect
          x={x(view[1]) - 3}
          y={1}
          width={6}
          height={26}
          fill="transparent"
          className="cursor-ew-resize"
          onPointerDown={(event) => down(event, "end")}
        />
      </g>
    </svg>
  );
}

/** 范围之间的过渡：220ms，ease-out。系统开启减少动态效果时直接跳到目标。 */
function useAnimatedView(view: View, setView: (view: View) => void) {
  const frame = useRef(0);
  const current = useRef(view);
  useEffect(() => {
    current.current = view;
  }, [view]);
  useEffect(() => () => cancelAnimationFrame(frame.current), []);
  return useCallback(
    (target: View) => {
      cancelAnimationFrame(frame.current);
      if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        setView(target);
        return;
      }
      const start = current.current;
      const begin = performance.now();
      const tick = (time: number) => {
        const p = Math.min(1, (time - begin) / 220);
        const e = 1 - Math.pow(1 - p, 3);
        setView([start[0] + (target[0] - start[0]) * e, start[1] + (target[1] - start[1]) * e]);
        if (p < 1) frame.current = requestAnimationFrame(tick);
      };
      frame.current = requestAnimationFrame(tick);
    },
    [setView],
  );
}
