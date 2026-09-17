import { useMemo } from "react";
import type { ReplayLane, TimelineRecord } from "@/api/types";
import { trackLabel } from "@/components/filters/TrackPicker";
import { TooltipReading, useTooltip } from "@/components/ui/Tooltip";
import { protocolRecordSummary } from "./protocolSummary";
import { layoutRanges } from "./rangeLayout";
import { DensityCurve } from "./DensityCurve";
import type { DensityScale } from "./densityScale";
import { formatTime, overlaps, percent, timeTicks, type TimeRange } from "./timeRange";

export interface PointSelection {
  trackId: string;
  from: string;
  to: string;
  count: number;
}
export interface RecordSelection {
  trackId: string;
  recordId: string;
}
interface Props {
  lane: ReplayLane;
  range: TimeRange;
  ticks: ReturnType<typeof timeTicks>;
  selected: RecordSelection | null;
  densityStatus: string | null;
  /** Shared by every point lane so their curve heights mean the same thing. */
  densityScale: DensityScale;
  onSelect: (value: RecordSelection) => void;
  onSelectPoints: (value: PointSelection) => void;
  expanded: boolean;
  onExpandedChange: (expanded: boolean) => void;
}

type RangeItem = ReturnType<typeof layoutRanges>["items"][number];

/** One observation drawn as a bar. Hovering reads out its window and protocol summary. */
function RangeSegment({
  lane,
  item,
  range,
  selected,
  onSelect,
}: {
  lane: ReplayLane;
  item: RangeItem;
  range: TimeRange;
  selected: RecordSelection | null;
  onSelect: (value: RecordSelection) => void;
}) {
  const tooltip = useTooltip();
  const summary = protocolRecordSummary(lane.track, item.record);
  const text = summary.title ?? summary.label;
  const reading = (
    <TooltipReading
      caption={`${formatTime(item.start, true)} – ${formatTime(item.end, true)}`}
      value={text}
    />
  );
  return (
    <button
      type="button"
      className={`timeline-range timeline-tone-${summary.tone ?? "default"}`}
      style={{
        left: `${percent(item.start, range)}%`,
        width: `${((item.end - item.start) / (range.end - range.start)) * 100}%`,
        top: `${8 + item.row * 28}px`,
      }}
      aria-label={text}
      aria-pressed={selected?.trackId === lane.track.id && selected.recordId === item.record.id}
      onPointerMove={(event) => tooltip.show(reading, event)}
      onPointerLeave={tooltip.hide}
      onClick={() => onSelect({ trackId: lane.track.id, recordId: item.record.id })}
    />
  );
}

/** A lane name that reads out in full on hover, but only while it is actually clipped. */
function LaneName({ text }: { text: string }) {
  const tooltip = useTooltip();
  return (
    <strong
      onPointerMove={(event) => {
        const element = event.currentTarget;
        if (element.scrollWidth > element.clientWidth + 1) tooltip.show(text, event);
        else tooltip.hide();
      }}
      onPointerLeave={tooltip.hide}
    >
      {text}
    </strong>
  );
}

function RecordPlot({
  lane,
  records,
  range,
  ticks,
  selected,
  densityStatus,
  densityScale,
  onSelect,
  onSelectPoints,
}: Props & { records: TimelineRecord[] }) {
  const layout = useMemo(
    () => layoutRanges(records, range.start, range.end),
    [records, range.start, range.end],
  );
  const hasDensity = Boolean(
    lane.counts?.buckets.some((bucket) =>
      overlaps(Date.parse(bucket.startedAt), Date.parse(bucket.endedAt), range),
    ) ||
    lane.detailCounts?.some((detail) =>
      detail.buckets.some((bucket) =>
        overlaps(Date.parse(bucket.startedAt), Date.parse(bucket.endedAt), range),
      ),
    ),
  );
  return (
    <div
      className="timeline-lane-plot"
      data-time-plot
      style={{ minHeight: `${Math.max(40, layout.rows * 28 + 12)}px` }}
    >
      {ticks.map((tick) => (
        <span
          key={tick.at}
          aria-hidden="true"
          className="timeline-grid-line"
          style={{ left: `${tick.left}%` }}
        />
      ))}
      {layout.items.map((item) => (
        <RangeSegment
          key={item.record.id}
          lane={lane}
          item={item}
          range={range}
          selected={selected}
          onSelect={onSelect}
        />
      ))}
      {lane.track.timeMode === "point" ? (
        <DensityCurve
          track={lane.track}
          counts={lane.counts}
          detailCounts={lane.detailCounts ?? []}
          range={range}
          scale={densityScale}
          onSelect={onSelectPoints}
        />
      ) : null}
      {!layout.items.length && !hasDensity ? (
        <span className="timeline-empty">
          {densityStatus && lane.track.timeMode === "point" ? densityStatus : null}
        </span>
      ) : null}
    </div>
  );
}

export function TimelineLane(props: Props) {
  const { lane, range, expanded, onExpandedChange } = props;
  const groups = useMemo(() => {
    const result = new Map<string, { id: string; label: string; records: TimelineRecord[] }>();
    for (const record of lane.records) {
      if (
        !overlaps(
          Date.parse(record.startedAt),
          Date.parse(record.endedAt ?? record.startedAt),
          range,
        )
      )
        continue;
      const group = protocolRecordSummary(lane.track, record).group;
      if (!group) continue;
      const current = result.get(group.id) ?? { ...group, records: [] };
      current.records.push(record);
      result.set(group.id, current);
    }
    return [...result.values()].sort((a, b) => a.label.localeCompare(b.label));
  }, [lane, range]);
  return (
    <>
      <div className="timeline-lane">
        <div className="timeline-lane-label">
          {groups.length ? (
            <button
              className="lane-expand"
              type="button"
              aria-expanded={expanded}
              aria-label={`${expanded ? "收起" : "展开"} ${lane.track.collectorDisplayName} 的应用`}
              onClick={() => onExpandedChange(!expanded)}
            >
              <span aria-hidden="true">{expanded ? "⌄" : "›"}</span>
              <LaneName text={trackLabel(lane.track)} />
              <small>{groups.length}</small>
            </button>
          ) : (
            <LaneName text={trackLabel(lane.track)} />
          )}
        </div>
        <RecordPlot {...props} records={lane.records} />
      </div>
      {expanded
        ? groups.map((group) => (
            <div className="timeline-lane application-sublane" key={group.id}>
              <div className="timeline-lane-label">
                <span className="application-initial" aria-hidden="true">
                  {group.label.slice(0, 1).toLocaleUpperCase()}
                </span>
                <LaneName text={group.label} />
              </div>
              <RecordPlot {...props} records={group.records} />
            </div>
          ))
        : null}
    </>
  );
}
