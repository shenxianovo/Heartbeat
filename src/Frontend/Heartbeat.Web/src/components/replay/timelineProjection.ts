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

function showsDensity(
  buckets: ReplayLane["counts"],
  detailCounts: ReplayLane["detailCounts"],
  lane: ReplayLane,
  densityStatus: string | null,
) {
  return Boolean(
    buckets?.buckets.length ||
    detailCounts?.some((counts) => counts.buckets.length) ||
    (lane.track.timeMode === "point" && densityStatus),
  );
}

function visibleLane(lane: ReplayLane, range: TimeRange, densityStatus: string | null) {
  const records = lane.records.filter((record) => recordOverlaps(record, range));
  const buckets =
    lane.counts?.buckets.filter((bucket) =>
      overlaps(Date.parse(bucket.startedAt), Date.parse(bucket.endedAt), range),
    ) ?? [];
  const detailCounts = lane.detailCounts?.filter((counts) =>
    overlaps(Date.parse(counts.from), Date.parse(counts.to), range),
  );
  const counts = lane.counts ? { ...lane.counts, buckets } : null;
  if (!records.length && !showsDensity(counts, detailCounts, lane, densityStatus)) return null;
  return {
    lane: {
      ...lane,
      records,
      counts,
      detailCounts,
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
