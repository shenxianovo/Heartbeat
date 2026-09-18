"use client";

import { useMemo, useRef, type KeyboardEvent, type PointerEvent } from "react";
import type { ReplayLane } from "@/api/types";
import {
  dragRange,
  formatTime,
  percent,
  overlaps,
  timeTicks,
  type RangeDrag,
  type TimeRange,
} from "./timeRange";
import { useFrameRange } from "./useFrameRange";

interface Props {
  lanes: ReplayLane[];
  bounds: TimeRange;
  range: TimeRange;
  onRange: (range: TimeRange) => void;
}

export function ActivityOverview({ lanes, bounds, range, onRange }: Props) {
  const frameRange = useFrameRange(onRange);
  const drag = useRef<{
    mode: RangeDrag;
    range: TimeRange;
    anchor: number;
    x: number;
    moved: boolean;
  } | null>(null);
  const full = range.start === bounds.start && range.end === bounds.end;
  const left = percent(range.start, bounds);
  const right = percent(range.end, bounds);
  /**
   * The whole-day picture only changes when the day or its Records change, so it is
   * drawn once and left alone while the Owner pans; only the shade and the handles
   * follow the range.
   */
  const { height, marks } = useMemo(() => {
    const rows = Array.from(new Set(lanes.map((lane) => lane.track.collectorId)));
    const svgHeight = Math.max(24, rows.length * 8 + 8);
    return {
      height: svgHeight,
      marks: (
        <svg viewBox={`0 0 1000 ${svgHeight}`} preserveAspectRatio="none" aria-hidden="true">
          {lanes.flatMap((lane) => {
            const y = 4 + rows.indexOf(lane.track.collectorId) * 8;
            const laneMarks = lane.counts
              ? lane.counts.buckets.map((bucket) => ({
                  id: String(bucket.index),
                  start: bucket.startedAt,
                  end: bucket.endedAt,
                }))
              : lane.records.map((record) => ({
                  id: record.id,
                  start: record.startedAt,
                  end: record.endedAt ?? record.startedAt,
                }));
            return laneMarks
              .filter((mark) => overlaps(Date.parse(mark.start), Date.parse(mark.end), bounds))
              .map((mark) => (
                <rect
                  key={`${lane.track.id}/${mark.id}`}
                  x={percent(Date.parse(mark.start), bounds) * 10}
                  y={y}
                  width={Math.max(
                    0.7,
                    (percent(Date.parse(mark.end), bounds) -
                      percent(Date.parse(mark.start), bounds)) *
                      10,
                  )}
                  height="6"
                  className={lane.counts ? "overview-point" : "overview-range"}
                />
              ));
          })}
        </svg>
      ),
    };
  }, [lanes, bounds]);
  function timeAt(event: PointerEvent<HTMLDivElement>) {
    const rect = event.currentTarget.getBoundingClientRect();
    return (
      bounds.start +
      Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)) *
        (bounds.end - bounds.start)
    );
  }
  function down(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    frameRange.cancel();
    event.preventDefault();
    const target = (event.target as HTMLElement).closest<HTMLElement>("[data-drag]");
    let mode = (target?.dataset.drag ?? "select") as RangeDrag;
    if (event.shiftKey || (mode === "move" && full)) mode = "select";
    drag.current = { mode, range, anchor: timeAt(event), x: event.clientX, moved: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function move(event: PointerEvent<HTMLDivElement>) {
    const active = drag.current;
    if (!active) return;
    if (Math.abs(event.clientX - active.x) > 3) active.moved = true;
    if (active.moved)
      frameRange.schedule(
        dragRange(active.mode, active.range, bounds, active.anchor, timeAt(event)),
      );
  }
  function up(event: PointerEvent<HTMLDivElement>) {
    const active = drag.current;
    if (active?.moved) frameRange.flush();
    else frameRange.cancel();
    if (active && !active.moved && active.mode === "select" && !full) {
      onRange(
        dragRange(
          "move",
          active.range,
          bounds,
          (active.range.start + active.range.end) / 2,
          timeAt(event),
        ),
      );
    }
    drag.current = null;
  }
  function key(event: KeyboardEvent<HTMLDivElement>, mode: RangeDrag) {
    if (event.key === "Escape") {
      event.preventDefault();
      onRange(bounds);
      return;
    }
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const anchor = mode === "end" ? range.end : range.start;
    const step = Math.max(1000, (range.end - range.start) * (event.shiftKey ? 0.1 : 0.02));
    const at =
      event.key === "Home"
        ? bounds.start
        : event.key === "End"
          ? bounds.end
          : anchor + (event.key === "ArrowLeft" ? -step : step);
    onRange(dragRange(mode, range, bounds, anchor, at));
  }
  return (
    <div className="activity-overview">
      <div className="overview-heading">
        <span>全天概览</span>
        <output aria-label="可见时间范围">
          {formatTime(range.start, true)} — {formatTime(range.end, true)}
          {range.end === bounds.end ? " · 结束" : ""}
        </output>
      </div>
      <div
        className="overview-track"
        data-testid="activity-overview"
        style={{ height }}
        onPointerDown={down}
        onPointerMove={move}
        onPointerUp={up}
        onPointerCancel={() => {
          frameRange.cancel();
          drag.current = null;
        }}
        onDoubleClick={() => onRange(bounds)}
      >
        {marks}
        <div className="overview-shade" style={{ left: 0, width: `${left}%` }} />
        <div className="overview-shade" style={{ left: `${right}%`, right: 0 }} />
        <div
          className={`overview-selection${full ? " full" : ""}`}
          style={{ left: `${left}%`, width: `${right - left}%` }}
          data-drag="move"
        >
          <div
            className="overview-move"
            tabIndex={0}
            role="slider"
            aria-label="移动时间范围"
            aria-valuemin={bounds.start}
            aria-valuemax={bounds.end - (range.end - range.start)}
            aria-valuenow={range.start}
            aria-valuetext={`${formatTime(range.start, true)} — ${formatTime(range.end, true)}`}
            onKeyDown={(event) => key(event, "move")}
          />
          {(["start", "end"] as const).map((mode) => (
            <div
              key={mode}
              className={`overview-handle ${mode}`}
              data-drag={mode}
              tabIndex={0}
              role="slider"
              aria-label={mode === "start" ? "范围起点" : "范围终点"}
              aria-valuemin={mode === "start" ? bounds.start : range.start + 1000}
              aria-valuemax={mode === "start" ? range.end - 1000 : bounds.end}
              aria-valuenow={range[mode]}
              aria-valuetext={formatTime(range[mode], true)}
              onKeyDown={(event) => key(event, mode)}
            />
          ))}
        </div>
      </div>
      <div className="overview-ticks">
        {timeTicks(bounds, 5).map((tick) => (
          <span key={tick.at} style={{ left: `${tick.left}%` }}>
            {tick.at === bounds.end ? "次日 " : ""}
            {tick.label}
          </span>
        ))}
      </div>
    </div>
  );
}
