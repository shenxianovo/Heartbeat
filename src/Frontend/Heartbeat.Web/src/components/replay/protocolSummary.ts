import type { TimelineRecord, TrackSummary } from "@/api/types";
import { summarizeRecord } from "@/components/records/renderers/registry";

export function protocolRecordSummary(track: TrackSummary, record: TimelineRecord) {
  return summarizeRecord(
    track.type,
    track.version,
    record.value,
    track.timeMode === "point" ? "瞬时记录" : "时间区间",
  );
}

export function protocolRecordLabel(track: TrackSummary, record: TimelineRecord): string {
  return protocolRecordSummary(track, record).label;
}

export function protocolRecordTitle(track: TrackSummary, record: TimelineRecord): string {
  const summary = protocolRecordSummary(track, record);
  return summary.title ?? summary.label;
}
