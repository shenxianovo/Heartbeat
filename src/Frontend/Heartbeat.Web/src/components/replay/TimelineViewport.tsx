"use client";

import { useMemo, useState } from "react";

import type { PointCountBucket, ReplayLane } from "@/api/types";
import { trackLabel } from "@/components/filters/TrackPicker";
import { RecordCard } from "@/components/records/RecordCard";
import { protocolRecordSummary } from "@/components/replay/protocolSummary";
import { layoutRanges } from "@/components/replay/rangeLayout";

interface PointSelection {
  trackId: string;
  from: string;
  to: string;
  count: number;
}

interface TimelineViewportProps {
  lanes: ReplayLane[];
  from: string;
  to: string;
  zoom: number;
  onSelectPoints: (selection: PointSelection) => void;
}

function position(at: string, from: number, span: number): number {
  return Math.max(0, Math.min(100, ((Date.parse(at) - from) / span) * 100));
}

export function TimelineViewport({ lanes, from, to, zoom, onSelectPoints }: TimelineViewportProps) {
  const [selection, setSelection] = useState<{
    trackId: string;
    recordId: string;
  } | null>(null);
  const start = Date.parse(from);
  const span = Math.max(1, Date.parse(to) - start);
  const selectedLane = lanes.find((lane) => lane.track.id === selection?.trackId);
  const selectedRecord = selectedLane?.records.find((record) => record.id === selection?.recordId);
  const selected =
    selectedLane && selectedRecord ? { track: selectedLane.track, record: selectedRecord } : null;
  const layouts = useMemo(
    () => lanes.map((lane) => ({ lane, layout: layoutRanges(lane.records, start, start + span) })),
    [lanes, start, span],
  );
  const maximumCount = useMemo(
    () =>
      lanes.reduce(
        (maximum, lane) =>
          lane.counts?.buckets.reduce((m, bucket) => Math.max(m, bucket.count), maximum) ?? maximum,
        1,
      ),
    [lanes],
  );

  return (
    <section className="timeline-card" aria-label="统一回放时间线">
      <div className="timeline-axis">
        <span>{new Date(from).toLocaleString()}</span>
        <strong>统一时间轴 · {zoom}×</strong>
        <span>{new Date(to).toLocaleString()}</span>
      </div>
      <div className="timeline-scroll">
        <div className="timeline-canvas" style={{ width: `${zoom * 100}%` }}>
          {layouts.map(({ lane, layout }) => (
            <div className="timeline-lane" key={lane.track.id}>
              <div className="timeline-lane-label">
                <strong>{trackLabel(lane.track)}</strong>
                <span>{lane.track.collectorDisplayName}</span>
              </div>
              <div
                className="timeline-lane-plot"
                style={{ minHeight: `${layout.rows * 3.2 + 1.6}rem` }}
              >
                {layout.items.map(({ record, row, start: recordStart, end }) => {
                  const left = ((recordStart - start) / span) * 100;
                  const width = ((end - recordStart) / span) * 100;
                  const summary = protocolRecordSummary(lane.track, record);
                  return (
                    <button
                      type="button"
                      className={`timeline-range timeline-tone-${summary.tone ?? "default"}`}
                      key={record.id}
                      style={{ left: `${left}%`, width: `${width}%`, top: `${0.8 + row * 3.2}rem` }}
                      title={summary.title ?? summary.label}
                      onClick={() => setSelection({ trackId: lane.track.id, recordId: record.id })}
                    >
                      <span>{summary.label}</span>
                    </button>
                  );
                })}
                {lane.counts?.buckets.map((bucket: PointCountBucket) => {
                  const left = position(bucket.startedAt, start, span);
                  const right = position(bucket.endedAt, start, span);
                  return (
                    <button
                      type="button"
                      className="timeline-density"
                      key={bucket.index}
                      style={{
                        left: `${left}%`,
                        width: `${right - left}%`,
                        opacity: 0.25 + (bucket.count / maximumCount) * 0.75,
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
                  );
                })}
                {!lane.records.length && !lane.counts?.buckets.length ? (
                  <span className="timeline-empty">此窗口没有记录</span>
                ) : null}
              </div>
            </div>
          ))}
        </div>
      </div>
      {selected ? (
        <div className="timeline-selection">
          <div className="list-summary">
            <span>所选区间</span>
            <button className="quiet-button" type="button" onClick={() => setSelection(null)}>
              关闭
            </button>
          </div>
          <RecordCard record={selected.record} track={selected.track} />
        </div>
      ) : null}
    </section>
  );
}
