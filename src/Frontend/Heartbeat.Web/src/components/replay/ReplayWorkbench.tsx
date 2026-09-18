"use client";

import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { useAuth } from "react-oidc-context";
import {
  usePointDensityTiles,
  useRecordsQuery,
  useReplayWindowQuery,
  useTracksQuery,
} from "@/api/queries";
import { Button } from "@/components/ui/Button";
import { DatePicker } from "@/components/ui/DatePicker";
import { MultiSelectPicker } from "@/components/ui/MultiSelectPicker";
import { Popover } from "@/components/ui/Popover";
import { Icon } from "@/components/ui/Icon";
import { DateRangeControls } from "@/components/filters/DateRangeControls";
import { trackLabel } from "@/components/filters/TrackPicker";
import { AppHeader } from "@/components/layout/AppHeader";
import { RecordsPanel } from "./RecordsPanel";
import { TimelineViewport } from "./TimelineViewport";
import type { PointSelection } from "./TimelineLane";
import { clampRange, densityBucketSeconds, type TimeRange } from "./timeRange";
import { cachedDensityLayers } from "./densityTiles";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { rangeToIso, todayRange, type DateRange } from "@/lib/dates";

export function ReplayWorkbench() {
  const auth = useAuth();
  const accessToken = auth.user?.access_token ?? "";
  const ownerSubject = auth.user?.profile.sub ?? "";
  const [selectedCollectorIds, setSelectedCollectorIds] = useState<string[] | null>(null);
  const [chosenRange, setChosenRange] = useState<DateRange | null>(null);
  const [viewport, setViewport] = useState<TimeRange | null>(null);
  const [detail, setDetail] = useState<PointSelection | null>(null);
  const hydrated = useSyncExternalStore(
    () => () => undefined,
    () => true,
    () => false,
  );
  const initialRange = useMemo(() => (hydrated ? todayRange() : null), [hydrated]);
  const chosen = chosenRange ?? initialRange;
  const iso = chosen ? rangeToIso(chosen) : null;
  const from = iso?.from ?? "";
  const to = iso?.to ?? "";
  const bounds = useMemo(
    () => (from && to ? { start: Date.parse(from), end: Date.parse(to) } : null),
    [from, to],
  );
  const range = useMemo(
    () => (bounds ? (viewport ? clampRange(viewport, bounds) : bounds) : null),
    [bounds, viewport],
  );
  const [settled, setSettled] = useState<TimeRange | null>(null);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(range), 150);
    return () => clearTimeout(timer);
  }, [range]);
  const zoomed = Boolean(
    range && bounds && (range.start !== bounds.start || range.end !== bounds.end),
  );
  const settledMatches = settled?.start === range?.start && settled?.end === range?.end;
  const tracksQuery = useTracksQuery(ownerSubject, accessToken);
  const allTracks = useMemo(() => tracksQuery.data?.tracks ?? [], [tracksQuery.data?.tracks]);
  const tracks = useMemo(
    () =>
      selectedCollectorIds === null
        ? allTracks
        : allTracks.filter((track) => selectedCollectorIds.includes(track.collectorId)),
    [allTracks, selectedCollectorIds],
  );
  const pointTracks = useMemo(() => tracks.filter((track) => track.timeMode === "point"), [tracks]);
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
  const replayQuery = useReplayWindowQuery(
    ownerSubject,
    accessToken,
    tracks,
    from,
    to,
    bounds ? densityBucketSeconds(bounds) : 900,
  );
  const densityQuery = usePointDensityTiles(
    ownerSubject,
    accessToken,
    pointTracks,
    bounds,
    zoomed && settled ? settled : null,
    settled ? densityBucketSeconds(settled) : 900,
  );
  const densityStatus =
    zoomed && pointTracks.length
      ? densityQuery.isError
        ? "密度读取失败"
        : !settledMatches || densityQuery.isPending
          ? "正在读取密度…"
          : null
      : null;
  const lanes = useMemo(
    () =>
      (replayQuery.data ?? []).map((lane) => ({
        ...lane,
        detailCounts:
          lane.track.timeMode === "point" && range
            ? cachedDensityLayers(densityQuery.layers, lane.track.id, range)
            : [],
      })),
    [replayQuery.data, densityQuery.layers, range],
  );
  const detailTrack = tracks.find((track) => track.id === detail?.trackId) ?? null;
  const detailQuery = useRecordsQuery(
    ownerSubject,
    accessToken,
    detailTrack ? (detail?.trackId ?? null) : null,
    detail?.from ?? "",
    detail?.to ?? "",
  );

  function setRange(next: TimeRange) {
    // Panning past the day's edge asks for the range it already shows; redrawing it
    // would repeat every lane for nothing.
    const clamped = bounds ? clampRange(next, bounds) : next;
    if (!range || range.start !== clamped.start || range.end !== clamped.end) setViewport(clamped);
    setDetail(null);
  }
  function chooseDate(next: DateRange) {
    setChosenRange(next);
    setViewport(null);
    setDetail(null);
  }
  function stepDay(step: number) {
    if (!chosen) return;
    const date = new Date(chosen.from);
    date.setDate(date.getDate() + step);
    chooseDate(todayRange(date));
  }
  async function refresh() {
    await tracksQuery.refetch();
    if (tracks.length) await replayQuery.refetch();
    if (pointTracks.length) await densityQuery.invalidate();
    if (detailTrack) await detailQuery.refetch();
  }
  const fetching = tracksQuery.isFetching || replayQuery.isFetching;
  return (
    <div className="app-frame">
      <AppHeader />
      <main className="workspace experience-workspace">
        <section className="workspace-heading">
          <div>
            <span className="eyebrow">DAY BY DAY</span>
            <h1>当天经历</h1>
            <p>沿着时间，回看一天的应用与交互。</p>
          </div>
          <Button variant="glass" type="button" disabled={fetching} onClick={() => void refresh()}>
            <Icon name="refresh" />
            {fetching ? "正在刷新" : "刷新"}
          </Button>
        </section>
        {tracksQuery.isPending ? (
          <LoadingState label="正在读取采集来源" />
        ) : tracksQuery.isError ? (
          <QueryState
            eyebrow="读取失败"
            title="暂时无法读取采集来源"
            description={tracksQuery.error.message}
            action={
              <Button variant="outline" type="button" onClick={() => void tracksQuery.refetch()}>
                重试
              </Button>
            }
          />
        ) : allTracks.length === 0 ? (
          <QueryState
            eyebrow="暂无来源"
            title="还没有可以回放的记录"
            description="采集端开始记录并完成上传后，对应来源会出现在这里。"
          />
        ) : chosen && bounds && range ? (
          <>
            <section className="experience-filters" aria-label="回放筛选">
              <div className="experience-date">
                <Button size="icon" aria-label="前一天" onClick={() => stepDay(-1)}>
                  <Icon name="chevronLeft" />
                </Button>
                <DatePicker
                  value={chosen.from.slice(0, 10)}
                  onChange={(value) => chooseDate(todayRange(new Date(`${value}T00:00:00`)))}
                />
                <Button size="icon" aria-label="后一天" onClick={() => stepDay(1)}>
                  <Icon name="chevronRight" />
                </Button>
              </div>
              <MultiSelectPicker
                label="采集来源"
                allLabel="全部来源"
                options={collectors.map(([value, label]) => ({ value, label }))}
                value={selectedCollectorIds ?? collectors.map(([id]) => id)}
                onChange={(ids) => {
                  setSelectedCollectorIds(ids.length === collectors.length ? null : ids);
                  setDetail(null);
                }}
              />
              <Popover
                label="自定义时间范围"
                className="range-picker"
                trigger={
                  <>
                    <Icon name="filter" />
                    <span>时间范围</span>
                    <Icon name="chevronDown" />
                  </>
                }
              >
                {(close) => (
                  <DateRangeControls
                    key={`${chosen.from}/${chosen.to}`}
                    value={chosen}
                    onApply={(next) => {
                      chooseDate(next);
                      close();
                    }}
                  />
                )}
              </Popover>
            </section>
            {!tracks.length ? (
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
                  <Button
                    variant="outline"
                    type="button"
                    onClick={() => void replayQuery.refetch()}
                  >
                    重试
                  </Button>
                }
              />
            ) : (
              <>
                {zoomed && settledMatches && densityQuery.isError ? (
                  <div className="density-error" role="alert">
                    当前范围的细节读取失败，仍显示已有密度。
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={() => void densityQuery.refetch()}
                    >
                      重试密度
                    </Button>
                  </div>
                ) : null}
                <TimelineViewport
                  key={`${ownerSubject}/${from}/${to}`}
                  lanes={lanes}
                  overviewLanes={replayQuery.data}
                  bounds={bounds}
                  range={range}
                  densityStatus={densityStatus}
                  onRange={setRange}
                  onSelectPoints={setDetail}
                />
              </>
            )}
            {detail && detailTrack ? (
              <section className="point-details glass-panel">
                <div className="section-heading">
                  <div>
                    <span className="track-source">所选密度区间 · {detail.count} 条</span>
                    <h2>{trackLabel(detailTrack)}</h2>
                  </div>
                  <Button variant="ghost" type="button" onClick={() => setDetail(null)}>
                    关闭详情
                  </Button>
                </div>
                <RecordsPanel query={detailQuery} selectedTrack={detailTrack} />
              </section>
            ) : null}
            <footer className="experience-footer">
              {Intl.DateTimeFormat().resolvedOptions().timeZone}
            </footer>
          </>
        ) : (
          <LoadingState label="正在准备时间范围" />
        )}
      </main>
    </div>
  );
}
