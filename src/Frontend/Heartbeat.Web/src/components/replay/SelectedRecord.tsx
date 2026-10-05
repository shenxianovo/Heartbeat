import type { TimelineRecord, TrackSummary } from "@/api/types";
import { RecordCard } from "@/components/records/RecordCard";
import { describeRecord, trackLabel } from "@/components/records/renderers/registry";
import { formatDateTime, formatDuration } from "@/lib/dates";

export function SelectedRecord({ record, track }: { record: TimelineRecord; track: TrackSummary }) {
  return (
    <>
      <dl className="timeline-readout">
        <div>
          <dt>记录</dt>
          <dd>
            <strong>{describeRecord(track, record).label}</strong>
          </dd>
          <dd className="readout-source">
            {track.collectorDisplayName} · {trackLabel(track)}
          </dd>
        </div>
        <div>
          <dt>开始</dt>
          <dd>
            <time dateTime={record.startedAt}>{formatDateTime(record.startedAt)}</time>
          </dd>
        </div>
        <div>
          <dt>结束</dt>
          <dd>
            {record.endedAt ? (
              <time dateTime={record.endedAt}>{formatDateTime(record.endedAt)}</time>
            ) : (
              "未提供结束时间"
            )}
          </dd>
        </div>
        <div>
          <dt>时长</dt>
          <dd>{formatDuration(record.startedAt, record.endedAt) ?? "未知"}</dd>
        </div>
      </dl>
      <details className="timeline-record-detail">
        <summary>记录内容与原始详情</summary>
        <RecordCard record={record} track={track} />
      </details>
    </>
  );
}
