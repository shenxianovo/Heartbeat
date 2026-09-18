import type { TimelineRecord, TrackSummary } from "@/api/types";
import type { RecordSummary } from "@/components/records/renderers/types";
import { summarizeRecord } from "@/components/records/renderers/registry";

/**
 * A Record's summary never changes, but a pan asks for it once per drawn observation
 * per step, so keep the parsed reading next to the Record instead of redoing it.
 */
const summaries = new WeakMap<TimelineRecord, { key: string; summary: RecordSummary }>();

export function protocolRecordSummary(track: TrackSummary, record: TimelineRecord): RecordSummary {
  const key = `${track.type}@${track.version}/${track.timeMode}`;
  const cached = summaries.get(record);
  if (cached?.key === key) return cached.summary;
  const summary = summarizeRecord(
    track.type,
    track.version,
    record.value,
    track.timeMode === "point" ? "瞬时记录" : "时间区间",
  );
  summaries.set(record, { key, summary });
  return summary;
}

export function protocolRecordLabel(track: TrackSummary, record: TimelineRecord): string {
  return protocolRecordSummary(track, record).label;
}

export function protocolRecordTitle(track: TrackSummary, record: TimelineRecord): string {
  const summary = protocolRecordSummary(track, record);
  return summary.title ?? summary.label;
}
