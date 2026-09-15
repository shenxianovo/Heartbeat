import { useMemo, useState } from "react";
import type { ReplayLane, TimelineRecord } from "@/api/types";
import { trackLabel } from "@/components/filters/TrackPicker";
import { protocolRecordSummary } from "./protocolSummary";
import { layoutRanges } from "./rangeLayout";
import { overlaps, percent, timeTicks, type TimeRange } from "./timeRange";

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
  onSelect: (value: RecordSelection) => void;
  onSelectPoints: (value: PointSelection) => void;
}

function RecordPlot({
  lane,
  records,
  range,
  ticks,
  selected,
  densityStatus,
  onSelect,
  onSelectPoints,
}: Props & { records: TimelineRecord[] }) {
  const layout = useMemo(
    () => layoutRanges(records, range.start, range.end),
    [records, range.start, range.end],
  );
  const buckets =
    lane.counts?.buckets.filter((bucket) =>
      overlaps(Date.parse(bucket.startedAt), Date.parse(bucket.endedAt), range),
    ) ?? [];
  const maximum = buckets.reduce((max, bucket) => Math.max(max, bucket.count), 1);
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
      {layout.items.map(({ record, row, start, end }) => {
        const summary = protocolRecordSummary(lane.track, record);
        return (
          <button
            type="button"
            className={`timeline-range timeline-tone-${summary.tone ?? "default"}`}
            key={record.id}
            style={{
              left: `${percent(start, range)}%`,
              width: `${((end - start) / (range.end - range.start)) * 100}%`,
              top: `${8 + row * 28}px`,
            }}
            title={summary.title ?? summary.label}
            aria-label={summary.title ?? summary.label}
            aria-pressed={selected?.trackId === lane.track.id && selected.recordId === record.id}
            onClick={() => onSelect({ trackId: lane.track.id, recordId: record.id })}
          />
        );
      })}
      {buckets.map((bucket) => (
        <button
          type="button"
          className="timeline-density"
          key={bucket.index}
          style={{
            left: `${percent(Date.parse(bucket.startedAt), range)}%`,
            width: `${percent(Date.parse(bucket.endedAt), range) - percent(Date.parse(bucket.startedAt), range)}%`,
            height: `${8 + (bucket.count / maximum) * 32}px`,
            opacity: 0.4 + (bucket.count / maximum) * 0.6,
          }}
          title={`${bucket.count} 条记录`}
          aria-label={`${trackLabel(lane.track)}，${bucket.count} 条记录`}
          onClick={() =>
            onSelectPoints({
              trackId: lane.track.id,
              from: bucket.startedAt,
              to: bucket.endedAt,
              count: bucket.count,
            })
          }
        />
      ))}
      {!layout.items.length && !buckets.length ? (
        <span className="timeline-empty">
          {densityStatus && lane.track.timeMode === "point" ? densityStatus : "此范围没有记录"}
        </span>
      ) : null}
    </div>
  );
}

export function TimelineLane(props: Props) {
  const { lane } = props;
  const [expanded, setExpanded] = useState(false);
  const groups = useMemo(() => {
    const result = new Map<string, { id: string; label: string; records: TimelineRecord[] }>();
    for (const record of lane.records) {
      const group = protocolRecordSummary(lane.track, record).group;
      if (!group) continue;
      const current = result.get(group.id) ?? { ...group, records: [] };
      current.records.push(record);
      result.set(group.id, current);
    }
    return [...result.values()].sort((a, b) => a.label.localeCompare(b.label));
  }, [lane]);
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
              onClick={() => setExpanded(!expanded)}
            >
              <span aria-hidden="true">{expanded ? "⌄" : "›"}</span>
              <strong title={trackLabel(lane.track)}>{trackLabel(lane.track)}</strong>
              <small>{groups.length}</small>
            </button>
          ) : (
            <strong>{trackLabel(lane.track)}</strong>
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
                <strong>{group.label}</strong>
              </div>
              <RecordPlot {...props} records={group.records} />
            </div>
          ))
        : null}
    </>
  );
}
