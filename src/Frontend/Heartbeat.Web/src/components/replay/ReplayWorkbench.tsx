"use client";

import { useMemo, useState, useSyncExternalStore } from "react";
import { useAuth } from "react-oidc-context";

import { useRecordsQuery, useTracksQuery } from "@/api/queries";
import type { TrackSummary } from "@/api/types";
import { DateRangeControls } from "@/components/filters/DateRangeControls";
import { TrackPicker, trackLabel } from "@/components/filters/TrackPicker";
import { AppHeader } from "@/components/layout/AppHeader";
import { RecordsPanel } from "@/components/replay/RecordsPanel";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { rangeToIso, todayRange, type DateRange } from "@/lib/dates";

function timeModeLabel(track: TrackSummary): string {
  if (track.timeMode === "point") return "时间点";
  if (track.endMode === "explicit") return "明确时间区间";
  return "连续时间区间";
}

export function ReplayWorkbench() {
  const auth = useAuth();
  const accessToken = auth.user?.access_token ?? "";
  const ownerSubject = auth.user?.profile.sub ?? "";
  const [selectedTrackId, setSelectedTrackId] = useState<string | null>(null);
  const [chosenRange, setChosenRange] = useState<DateRange | null>(null);
  const hydrated = useSyncExternalStore(
    () => () => undefined,
    () => true,
    () => false,
  );
  const initialRange = useMemo(() => (hydrated ? todayRange() : null), [hydrated]);
  const range = chosenRange ?? initialRange;
  const tracksQuery = useTracksQuery(ownerSubject, accessToken);

  const selectedTrack = useMemo(() => {
    const tracks = tracksQuery.data?.tracks ?? [];
    return tracks.find((track) => track.id === selectedTrackId) ?? tracks[0] ?? null;
  }, [selectedTrackId, tracksQuery.data?.tracks]);
  const effectiveTrackId = selectedTrack?.id ?? null;
  const isoRange = range ? rangeToIso(range) : null;
  const recordsQuery = useRecordsQuery(
    ownerSubject,
    accessToken,
    effectiveTrackId,
    isoRange?.from ?? "",
    isoRange?.to ?? "",
  );

  async function refresh() {
    await tracksQuery.refetch();
    if (effectiveTrackId) await recordsQuery.refetch();
  }

  return (
    <div className="app-frame">
      <AppHeader />
      <main className="workspace">
        <section className="workspace-heading">
          <div>
            <span className="eyebrow">TIMELINE REPLAY</span>
            <h1>回放记录</h1>
            <p>选择采集来源和时间范围，沿时间顺序查看保存下来的记录。</p>
          </div>
          <button
            className="refresh-button"
            type="button"
            disabled={tracksQuery.isFetching || recordsQuery.isFetching}
            onClick={() => void refresh()}
          >
            <span aria-hidden="true">↻</span>
            {tracksQuery.isFetching || recordsQuery.isFetching ? "正在刷新" : "刷新"}
          </button>
        </section>

        {tracksQuery.isPending ? (
          <LoadingState label="正在读取采集来源" />
        ) : tracksQuery.isError ? (
          <QueryState
            eyebrow="读取失败"
            title="暂时无法读取采集来源"
            description={tracksQuery.error.message}
            action={
              <button
                className="secondary-button"
                type="button"
                onClick={() => void tracksQuery.refetch()}
              >
                重试
              </button>
            }
          />
        ) : tracksQuery.data.tracks.length === 0 ? (
          <QueryState
            eyebrow="暂无来源"
            title="还没有可以回放的记录"
            description="采集端开始记录并完成上传后，对应来源会出现在这里。"
          />
        ) : range && selectedTrack ? (
          <>
            <section className="filter-panel" aria-label="回放筛选">
              <TrackPicker
                tracks={tracksQuery.data.tracks}
                value={effectiveTrackId}
                onChange={setSelectedTrackId}
              />
              <DateRangeControls value={range} onApply={setChosenRange} />
            </section>

            <section className="track-heading">
              <div>
                <span className="track-source">{selectedTrack.collectorDisplayName}</span>
                <h2>{trackLabel(selectedTrack)}</h2>
              </div>
              <div className="track-badges">
                <span>{timeModeLabel(selectedTrack)}</span>
                <span title={selectedTrack.collectorTarget}>{selectedTrack.collectorTarget}</span>
              </div>
            </section>

            <RecordsPanel query={recordsQuery} selectedTrack={selectedTrack} />
          </>
        ) : (
          <LoadingState label="正在准备时间范围" />
        )}
      </main>
    </div>
  );
}
