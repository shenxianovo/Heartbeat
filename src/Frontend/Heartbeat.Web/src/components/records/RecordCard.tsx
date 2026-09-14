"use client";

import { useState } from "react";

import type { TimelineRecord, TrackReference } from "@/api/types";
import { RecordValue } from "@/components/records/RecordValue";
import { stringifyJson } from "@/components/records/renderers/FallbackJsonRenderer";
import { formatDateTime, formatDuration } from "@/lib/dates";

interface RecordCardProps {
  record: TimelineRecord;
  track: TrackReference;
}

export function RecordCard({ record, track }: RecordCardProps) {
  const [expanded, setExpanded] = useState(false);
  const duration = formatDuration(record.startedAt, record.endedAt);

  return (
    <article className="record-row">
      <div className="record-time" aria-label={`开始于 ${formatDateTime(record.startedAt)}`}>
        <strong>{formatDateTime(record.startedAt)}</strong>
        <span>
          {record.endedAt
            ? `至 ${formatDateTime(record.endedAt)}`
            : track.timeMode === "point"
              ? "时间点"
              : "未提供结束时间"}
        </span>
      </div>
      <div className="timeline-node" aria-hidden="true">
        <span />
      </div>
      <div className="record-content">
        <div className="record-meta">
          <span>{duration ?? (track.timeMode === "point" ? "瞬时记录" : "未提供结束时间")}</span>
          <span>
            {track.type} · v{track.version}
          </span>
        </div>

        <RecordValue type={track.type} version={track.version} value={record.value} />

        <button
          className="detail-toggle"
          type="button"
          aria-expanded={expanded}
          onClick={() => setExpanded((value) => !value)}
        >
          {expanded ? "收起详情" : "查看详情"}
          <span aria-hidden="true">{expanded ? "−" : "+"}</span>
        </button>

        {expanded ? (
          <div className="record-details">
            <dl>
              <div>
                <dt>Record ID</dt>
                <dd>{record.id}</dd>
              </div>
              <div>
                <dt>观察时间</dt>
                <dd>{record.observedAt ? formatDateTime(record.observedAt) : "与开始时间相同"}</dd>
              </div>
              <div>
                <dt>接收时间</dt>
                <dd>{formatDateTime(record.receivedAt)}</dd>
              </div>
            </dl>
            <div className="raw-json">
              <span>原始 JSON</span>
              <pre>{stringifyJson(record.value)}</pre>
            </div>
          </div>
        ) : null}
      </div>
    </article>
  );
}
