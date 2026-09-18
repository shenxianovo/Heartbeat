import { useMemo } from "react";
import type { ReplayLane, TimelineRecord } from "@/api/types";
import { trackLabel } from "@/components/filters/TrackPicker";
import { useTooltip } from "@/components/ui/Tooltip";
import { describeRecord } from "@/components/records/renderers/registry";
import { laneHeight, layoutRanges } from "./rangeLayout";
import { DensityCurve } from "./DensityCurve";
import { CanvasRanges } from "./CanvasRanges";
import { RangeBars } from "./RangeBars";
import type { DensityScale } from "./densityScale";
import { overlaps, timeTicks, type TimeRange } from "./timeRange";

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

/** Below this count, individual DOM buttons keep direct keyboard access. */
const MAX_RANGE_BUTTONS = 400;

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

/**
 * What an empty window says. Density buckets count as content even though they are
 * not records, and only a point lane has a status worth explaining.
 */
function EmptyWindow({
  lane,
  range,
  status,
}: {
  lane: ReplayLane;
  range: TimeRange;
  status: string | null;
}) {
  const inWindow = (buckets: { startedAt: string; endedAt: string }[]) =>
    buckets.some((bucket) =>
      overlaps(Date.parse(bucket.startedAt), Date.parse(bucket.endedAt), range),
    );
  if (lane.counts && inWindow(lane.counts.buckets)) return null;
  if (lane.detailCounts?.some((detail) => inWindow(detail.buckets))) return null;
  return <span className="timeline-empty">{lane.track.timeMode === "point" ? status : null}</span>;
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
  const medium = {
    track: lane.track,
    items: layout.items,
    range,
    selectedId: selected?.trackId === lane.track.id ? selected.recordId : null,
    onSelect: (recordId: string) => onSelect({ trackId: lane.track.id, recordId }),
  };
  return (
    <div
      className="timeline-lane-plot"
      data-time-plot
      style={{ minHeight: `${laneHeight(layout.rows)}px` }}
    >
      {ticks.map((tick) => (
        <span
          key={tick.at}
          aria-hidden="true"
          className="timeline-grid-line"
          style={{ left: `${tick.left}%` }}
        />
      ))}
      {/* One surface costs a redraw per frame; one node per observation costs a layout pass. */}
      {layout.items.length > MAX_RANGE_BUTTONS ? (
        <CanvasRanges {...medium} />
      ) : (
        <RangeBars {...medium} />
      )}
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
      {layout.items.length ? null : (
        <EmptyWindow lane={lane} range={range} status={densityStatus} />
      )}
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
      const group = describeRecord(lane.track, record).group;
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
