import type { ReplayLane, TimelineRecord, TrackSummary } from "@/api/types";
import { overlaps, type TimeRange } from "./timeRange";

export interface ProjectedTimelineLane {
  lane: ReplayLane;
}

export interface ProjectedCollector {
  id: string;
  name: string;
  lanes: ProjectedTimelineLane[];
}

function recordOverlaps(record: TimelineRecord, range: TimeRange): boolean {
  return overlaps(
    Date.parse(record.startedAt),
    Date.parse(record.endedAt ?? record.startedAt),
    range,
  );
}

function visibleLane(lane: ReplayLane, range: TimeRange, densityStatus: string | null) {
  const records = lane.records.filter((record) => recordOverlaps(record, range));
  const buckets =
    lane.counts?.buckets.filter((bucket) =>
      overlaps(Date.parse(bucket.startedAt), Date.parse(bucket.endedAt), range),
    ) ?? [];
  const showsDensityStatus = lane.track.timeMode === "point" && Boolean(densityStatus);
  if (!records.length && !buckets.length && !showsDensityStatus) return null;
  return {
    lane: {
      ...lane,
      records,
      counts: lane.counts ? { ...lane.counts, buckets } : null,
    },
    records,
  };
}

export function projectTimeline(
  lanes: ReplayLane[],
  range: TimeRange,
  densityStatus: string | null,
) {
  const groups = new Map<string, ProjectedCollector>();
  const visibleRecords: { track: TrackSummary; record: TimelineRecord }[] = [];

  for (const lane of lanes) {
    const projected = visibleLane(lane, range, densityStatus);
    if (!projected) continue;

    const { collectorId, collectorDisplayName, collectorTarget } = lane.track;
    const group = groups.get(collectorId) ?? {
      id: collectorId,
      name: collectorDisplayName || collectorTarget,
      lanes: [],
    };
    group.lanes.push({ lane: projected.lane });
    groups.set(collectorId, group);

    visibleRecords.push(...projected.records.map((record) => ({ track: lane.track, record })));
  }

  const byTime = (a: { record: TimelineRecord }, b: { record: TimelineRecord }) =>
    Date.parse(a.record.startedAt) - Date.parse(b.record.startedAt) ||
    a.record.id.localeCompare(b.record.id);
  visibleRecords.sort(byTime);
  return { groups: [...groups.values()], visibleRecords };
}
