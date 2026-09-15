"use client";

import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import { useEffect, useMemo, useRef, useState, type PointerEvent } from "react";
import type { ReplayLane } from "@/api/types";
import { trackLabel } from "@/components/filters/TrackPicker";
import { RecordCard } from "@/components/records/RecordCard";
import { ActivityOverview } from "./ActivityOverview";
import { TimelineLane, type PointSelection, type RecordSelection } from "./TimelineLane";
import { protocolRecordSummary } from "./protocolSummary";
import {
  clampRange,
  dragRange,
  formatTime,
  overlaps,
  timeTicks,
  zoomRange,
  type TimeRange,
} from "./timeRange";

interface Props {
  lanes: ReplayLane[];
  overviewLanes: ReplayLane[];
  bounds: TimeRange;
  range: TimeRange;
  densityStatus: string | null;
  onRange: (range: TimeRange) => void;
  onSelectPoints: (selection: PointSelection | null) => void;
}

export function TimelineViewport({
  lanes,
  overviewLanes,
  bounds,
  range,
  densityStatus,
  onRange,
  onSelectPoints,
}: Props) {
  const [selection, setSelection] = useState<RecordSelection | null>(null);
  const [paging, setPaging] = useState({ key: "", page: 0 });
  const pageKey = `${range.start}/${range.end}/${lanes.map((lane) => lane.track.id).join(",")}`;
  const page = paging.key === pageKey ? paging.page : 0;
  const setPage = (page: number) => setPaging({ key: pageKey, page });
  const ruler = useRef<HTMLDivElement>(null);
  const [tickCount, setTickCount] = useState(6);
  const timeline = useRef<HTMLDivElement>(null);
  const drag = useRef<{
    x: number;
    left: number;
    width: number;
    range: TimeRange;
    brush: boolean;
    moved: boolean;
  } | null>(null);
  const suppressClick = useRef(false);
  const groups = useMemo(() => {
    const result = new Map<string, { name: string; lanes: ReplayLane[] }>();
    for (const lane of lanes) {
      const { collectorId, collectorDisplayName, collectorTarget } = lane.track;
      const group = result.get(collectorId) ?? {
        name: collectorDisplayName || collectorTarget,
        lanes: [],
      };
      group.lanes.push(lane);
      result.set(collectorId, group);
    }
    return [...result.entries()];
  }, [lanes]);
  const visibleRecords = useMemo(
    () =>
      lanes
        .flatMap((lane) =>
          lane.records
            .filter((record) =>
              overlaps(
                Date.parse(record.startedAt),
                Date.parse(record.endedAt ?? record.startedAt),
                range,
              ),
            )
            .map((record) => ({ track: lane.track, record })),
        )
        .sort(
          (a, b) =>
            Date.parse(a.record.startedAt) - Date.parse(b.record.startedAt) ||
            a.record.id.localeCompare(b.record.id),
        ),
    [lanes, range],
  );
  const selected = visibleRecords.find(
    (item) => item.track.id === selection?.trackId && item.record.id === selection.recordId,
  );
  const pageCount = Math.ceil(visibleRecords.length / 8);
  const currentPage = Math.min(page, Math.max(0, pageCount - 1));
  const ticks = timeTicks(range, tickCount);
  useEffect(() => {
    const element = ruler.current;
    if (!element || typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() =>
      setTickCount(Math.max(2, Math.floor(element.clientWidth / 80))),
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  function select(value: RecordSelection) {
    setSelection(value);
    onSelectPoints(null);
    const index = visibleRecords.findIndex(
      (item) => item.track.id === value.trackId && item.record.id === value.recordId,
    );
    if (index >= 0) setPage(Math.floor(index / 8));
  }
  function focus(start: number, end: number) {
    const pad = Math.max(1000, (end - start) * 0.2);
    onRange(clampRange({ start: start - pad, end: end + pad }, bounds));
  }
  function down(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    const plot = (event.target as HTMLElement).closest<HTMLElement>("[data-time-plot]");
    if (!plot) return;
    const rect = plot.getBoundingClientRect();
    suppressClick.current = false;
    drag.current = {
      x: event.clientX,
      left: rect.left,
      width: rect.width,
      range,
      brush: event.shiftKey,
      moved: false,
    };
  }
  function move(event: PointerEvent<HTMLDivElement>) {
    const active = drag.current;
    if (!active) return;
    if (Math.abs(event.clientX - active.x) <= 4 && !active.moved) return;
    active.moved = true;
    event.currentTarget.setPointerCapture(event.pointerId);
    const span = active.range.end - active.range.start;
    const anchor = active.range.start + ((active.x - active.left) / active.width) * span;
    const delta = ((event.clientX - active.x) / active.width) * span;
    onRange(
      dragRange(
        active.brush ? "select" : "move",
        active.range,
        bounds,
        anchor,
        anchor + (active.brush ? delta : -delta),
      ),
    );
  }
  useEffect(() => {
    const element = timeline.current;
    if (!element) return;
    function wheel(event: WheelEvent) {
      const plot = (event.target as HTMLElement).closest<HTMLElement>("[data-time-plot]");
      if (!plot || (!event.ctrlKey && !event.metaKey)) return;
      event.preventDefault();
      const rect = plot.getBoundingClientRect();
      const pivot = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
      onRange(
        zoomRange(range, bounds, Math.exp(Math.max(-1, Math.min(1, event.deltaY * 0.002))), pivot),
      );
    }
    element.addEventListener("wheel", wheel, { passive: false });
    return () => element.removeEventListener("wheel", wheel);
  }, [bounds, range, onRange]);

  return (
    <>
      <section className="timeline-card" aria-label="统一回放时间线">
        <div className="swimlane-toolbar">
          <div>
            <h2>活动泳道</h2>
            <p>拖动平移 · Ctrl/Command + 滚轮缩放 · Shift 拖选</p>
          </div>
          <div className="timeline-tools" aria-label="时间轴缩放">
            <Button
              type="button"
              variant="ghost"
              aria-label="向前平移"
              onClick={() =>
                onRange(dragRange("move", range, bounds, 0, -(range.end - range.start) * 0.5))
              }
            >
              <Icon name="chevronLeft" />
            </Button>
            <Button
              type="button"
              variant="ghost"
              aria-label="缩小时间轴"
              onClick={() => onRange(zoomRange(range, bounds, 2))}
            >
              <Icon name="minus" />
            </Button>
            <Button
              type="button"
              variant="ghost"
              aria-label="放大时间轴"
              onClick={() => onRange(zoomRange(range, bounds, 0.5))}
            >
              <Icon name="plus" />
            </Button>
            <Button
              type="button"
              variant="ghost"
              aria-label="向后平移"
              onClick={() =>
                onRange(dragRange("move", range, bounds, 0, (range.end - range.start) * 0.5))
              }
            >
              <Icon name="chevronRight" />
            </Button>
            <Button type="button" variant="glass" onClick={() => onRange(bounds)}>
              全天
            </Button>
          </div>
        </div>
        <ActivityOverview lanes={overviewLanes} bounds={bounds} range={range} onRange={onRange} />
        <div
          ref={timeline}
          className="swimlane-timeline"
          tabIndex={0}
          role="region"
          aria-label="活动泳道，方向键平移，加减键缩放"
          onPointerDown={down}
          onPointerMove={move}
          onPointerUp={() => {
            suppressClick.current = drag.current?.moved ?? false;
            drag.current = null;
          }}
          onPointerCancel={() => {
            drag.current = null;
            suppressClick.current = false;
          }}
          onClickCapture={(event) => {
            if (suppressClick.current && event.detail > 0) {
              event.preventDefault();
              event.stopPropagation();
              suppressClick.current = false;
            }
          }}
          onKeyDown={(event) => {
            if (event.target !== event.currentTarget) return;
            if (["+", "=", "-", "ArrowLeft", "ArrowRight", "Escape"].includes(event.key))
              event.preventDefault();
            if (["+", "=", "-"].includes(event.key))
              onRange(zoomRange(range, bounds, event.key === "-" ? 2 : 0.5));
            else if (["ArrowLeft", "ArrowRight"].includes(event.key))
              onRange(
                dragRange(
                  "move",
                  range,
                  bounds,
                  0,
                  (range.end - range.start) * (event.key === "ArrowLeft" ? -0.2 : 0.2),
                ),
              );
            else if (event.key === "Escape") onRange(bounds);
          }}
        >
          <div className="swimlane-scroll">
            <div className="timeline-ruler-row">
              <div className="timeline-ruler-label">时间</div>
              <div ref={ruler} className="timeline-ruler">
                {ticks.map((tick) => (
                  <span key={tick.at} style={{ left: `${tick.left}%` }}>
                    {tick.label}
                  </span>
                ))}
              </div>
            </div>
            {groups.map(([id, group]) => (
              <section className="collector-swimlanes" key={id} aria-label={group.name}>
                <h3 className="collector-heading">
                  <Icon name="monitor" />
                  {group.name}
                  <small>{group.lanes.length} 条轨道</small>
                </h3>
                {group.lanes.map((lane) => (
                  <TimelineLane
                    key={lane.track.id}
                    lane={lane}
                    range={range}
                    ticks={ticks}
                    selected={selected ? selection : null}
                    densityStatus={densityStatus}
                    onSelect={select}
                    onSelectPoints={(value) => {
                      setSelection(null);
                      onSelectPoints(value);
                    }}
                  />
                ))}
              </section>
            ))}
          </div>
        </div>
        <p className="timeline-note">
          空白表示没有可用记录，不代表没有活动。输入密度表示已存事件数量。
        </p>
      </section>
      {selected ? (
        <section className="timeline-selection glass-panel" aria-label="所选记录详情">
          <div className="section-heading">
            <h2>所选区间</h2>
            <div>
              <Button
                variant="ghost"
                type="button"
                onClick={() =>
                  focus(
                    Date.parse(selected.record.startedAt),
                    Date.parse(selected.record.endedAt ?? selected.record.startedAt),
                  )
                }
              >
                聚焦此记录
              </Button>
              <Button variant="ghost" type="button" onClick={() => setSelection(null)}>
                关闭
              </Button>
            </div>
          </div>
          <RecordCard
            key={`${selected.track.id}/${selected.record.id}`}
            record={selected.record}
            track={selected.track}
          />
        </section>
      ) : null}
      <section className="range-records" aria-label="区间记录列表">
        <div className="section-heading">
          <div>
            <h2>
              记录 <small>{visibleRecords.length}</small>
            </h2>
            <p>当前范围内的区间记录；输入等瞬时记录可点击密度查看。</p>
          </div>
        </div>
        <div className="experience-record-grid">
          {visibleRecords.slice(currentPage * 8, currentPage * 8 + 8).map(({ track, record }) => (
            <button
              className="experience-record"
              type="button"
              key={`${track.id}/${record.id}`}
              aria-pressed={selected?.track.id === track.id && selected.record.id === record.id}
              onClick={() => select({ trackId: track.id, recordId: record.id })}
            >
              <span className="experience-record-source">
                {track.collectorDisplayName} · {trackLabel(track)}
              </span>
              <strong>{protocolRecordSummary(track, record).label}</strong>
              <time>
                {formatTime(Date.parse(record.startedAt), true)} —{" "}
                {record.endedAt ? formatTime(Date.parse(record.endedAt), true) : "结束未知"}
              </time>
            </button>
          ))}
        </div>
        {!visibleRecords.length ? <p className="timeline-note">此范围没有区间记录。</p> : null}
        {pageCount > 1 ? (
          <nav className="record-pagination" aria-label="区间记录分页">
            <Button
              type="button"
              variant="ghost"
              disabled={currentPage === 0}
              onClick={() => setPage(currentPage - 1)}
            >
              上一页
            </Button>
            <span>
              {currentPage + 1} / {pageCount}
            </span>
            <Button
              type="button"
              variant="ghost"
              disabled={currentPage + 1 === pageCount}
              onClick={() => setPage(currentPage + 1)}
            >
              下一页
            </Button>
          </nav>
        ) : null}
      </section>
    </>
  );
}
