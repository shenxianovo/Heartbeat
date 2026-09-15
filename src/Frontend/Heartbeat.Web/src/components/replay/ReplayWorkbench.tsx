"use client";

import { useMemo, useState, useSyncExternalStore } from "react";
import { useAuth } from "react-oidc-context";

import { useRecordsQuery, useReplayWindowQuery, useTracksQuery } from "@/api/queries";
import { DateRangeControls } from "@/components/filters/DateRangeControls";
import { AppHeader } from "@/components/layout/AppHeader";
import { RecordsPanel } from "@/components/replay/RecordsPanel";
import { TimelineViewport } from "@/components/replay/TimelineViewport";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { rangeToIso, todayRange, type DateRange } from "@/lib/dates";

interface DetailWindow {
  trackId: string;
  from: string;
  to: string;
  count: number;
}

const zoomOptions = [
  { zoom: 1, bucketSeconds: 900 },
  { zoom: 2, bucketSeconds: 300 },
  { zoom: 4, bucketSeconds: 60 },
] as const;

export function ReplayWorkbench() {
  const auth = useAuth();
  const accessToken = auth.user?.access_token ?? "";
  const ownerSubject = auth.user?.profile.sub ?? "";
  const [selectedCollectorIds, setSelectedCollectorIds] = useState<string[] | null>(null);
  const [chosenRange, setChosenRange] = useState<DateRange | null>(null);
  const [zoomIndex, setZoomIndex] = useState(0);
  const [detail, setDetail] = useState<DetailWindow | null>(null);
  const hydrated = useSyncExternalStore(
    () => () => undefined,
    () => true,
    () => false,
  );
  const initialRange = useMemo(() => (hydrated ? todayRange() : null), [hydrated]);
  const range = chosenRange ?? initialRange;
  const tracksQuery = useTracksQuery(ownerSubject, accessToken);
  const allTracks = useMemo(() => tracksQuery.data?.tracks ?? [], [tracksQuery.data?.tracks]);
  const tracks = useMemo(
    () =>
      selectedCollectorIds === null
        ? allTracks
        : allTracks.filter((track) => selectedCollectorIds.includes(track.collectorId)),
    [allTracks, selectedCollectorIds],
  );
  const collectors = useMemo(
    () =>
      Array.from(
        new Map(
          allTracks.map((track) => [
            track.collectorId,
            track.collectorDisplayName || track.collectorTarget,
          ]),
        ),
      ),
    [allTracks],
  );
  const isoRange = range ? rangeToIso(range) : null;
  const zoom = zoomOptions[zoomIndex] ?? zoomOptions[0];
  const replayQuery = useReplayWindowQuery(
    ownerSubject,
    accessToken,
    tracks,
    isoRange?.from ?? "",
    isoRange?.to ?? "",
    zoom.bucketSeconds,
  );
  const detailTrack = allTracks.find((track) => track.id === detail?.trackId) ?? null;
  const detailQuery = useRecordsQuery(
    ownerSubject,
    accessToken,
    detail?.trackId ?? null,
    detail?.from ?? "",
    detail?.to ?? "",
  );

  async function refresh() {
    await tracksQuery.refetch();
    if (tracks.length) await replayQuery.refetch();
    if (detail) await detailQuery.refetch();
  }

  const fetching = tracksQuery.isFetching || replayQuery.isFetching || detailQuery.isFetching;

  return (
    <div className="app-frame">
      <AppHeader />
      <main className="workspace">
        <section className="workspace-heading">
          <div>
            <span className="eyebrow">TIMELINE REPLAY</span>
            <h1>回放记录</h1>
            <p>在同一时间窗口里对齐应用、离开状态、输入密度和观察状态。</p>
          </div>
          <button
            className="refresh-button"
            type="button"
            disabled={fetching}
            onClick={() => void refresh()}
          >
            <span aria-hidden="true">↻</span>
            {fetching ? "正在刷新" : "刷新"}
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
        ) : allTracks.length === 0 ? (
          <QueryState
            eyebrow="暂无来源"
            title="还没有可以回放的记录"
            description="采集端开始记录并完成上传后，对应来源会出现在这里。"
          />
        ) : range && isoRange ? (
          <>
            <section className="filter-panel timeline-filters" aria-label="回放筛选">
              <label className="field track-field">
                <span>采集来源</span>
                <select
                  multiple
                  value={selectedCollectorIds ?? collectors.map(([id]) => id)}
                  onChange={(event) => {
                    const ids = Array.from(event.target.selectedOptions, (option) => option.value);
                    setSelectedCollectorIds(ids.length === collectors.length ? null : ids);
                    setDetail(null);
                  }}
                >
                  {collectors.map(([id, label]) => (
                    <option key={id} value={id}>
                      {label}
                    </option>
                  ))}
                </select>
                <button
                  className="quiet-button"
                  type="button"
                  onClick={() => {
                    setSelectedCollectorIds(null);
                    setDetail(null);
                  }}
                >
                  选择全部来源
                </button>
              </label>
              <DateRangeControls
                value={range}
                onApply={(next) => {
                  setChosenRange(next);
                  setDetail(null);
                }}
              />
              <div className="zoom-controls" aria-label="时间轴缩放">
                <span>缩放与密度粒度</span>
                <div>
                  {zoomOptions.map((option, index) => (
                    <button
                      key={option.zoom}
                      className={
                        index === zoomIndex ? "secondary-button zoom-active" : "secondary-button"
                      }
                      type="button"
                      aria-pressed={index === zoomIndex}
                      onClick={() => {
                        setZoomIndex(index);
                        setDetail(null);
                      }}
                    >
                      {option.zoom}× · {option.bucketSeconds / 60} 分钟
                    </button>
                  ))}
                </div>
              </div>
            </section>

            {tracks.length === 0 ? (
              <QueryState
                eyebrow="暂无选择"
                title="尚未选择来源"
                description="选择一个或多个来源，在同一时间轴中组合查看。"
              />
            ) : replayQuery.isPending ? (
              <LoadingState label="正在构建统一时间窗口" />
            ) : replayQuery.isError ? (
              <QueryState
                eyebrow="读取失败"
                title="暂时无法显示时间线"
                description={replayQuery.error.message}
                action={
                  <button
                    className="secondary-button"
                    type="button"
                    onClick={() => void replayQuery.refetch()}
                  >
                    重试
                  </button>
                }
              />
            ) : (
              <TimelineViewport
                lanes={replayQuery.data}
                from={isoRange.from}
                to={isoRange.to}
                zoom={zoom.zoom}
                onSelectPoints={setDetail}
              />
            )}

            {detail && detailTrack ? (
              <section className="point-details">
                <div className="track-heading">
                  <div>
                    <span className="track-source">所选密度桶 · {detail.count} 条</span>
                    <h2>
                      {detailTrack.type} · v{detailTrack.version}
                    </h2>
                  </div>
                  <button className="quiet-button" type="button" onClick={() => setDetail(null)}>
                    关闭详情
                  </button>
                </div>
                <RecordsPanel query={detailQuery} selectedTrack={detailTrack} />
              </section>
            ) : null}
          </>
        ) : (
          <LoadingState label="正在准备时间范围" />
        )}
      </main>
    </div>
  );
}
