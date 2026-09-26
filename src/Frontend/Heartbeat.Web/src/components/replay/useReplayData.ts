import { useEffect, useMemo, useState } from "react";
import { useRecordsQuery, useReplayWindowQuery, useTracksQuery } from "@/api/queries";
import { usePointDensityTiles } from "@/api/densityQueries";
import type { ReplayLane, TrackSummary } from "@/api/types";
import { densityBucketSeconds, type TimeRange } from "./timeRange";
import { cachedDensityLayers } from "./densityTiles";
import type { useReplaySelection } from "./useReplaySelection";

function useSettledRange(scope: string, range: TimeRange | null) {
  const [settled, setSettled] = useState<{ scope: string; range: TimeRange | null } | null>(null);
  useEffect(() => {
    const timer = setTimeout(() => setSettled({ scope, range }), 150);
    return () => clearTimeout(timer);
  }, [scope, range]);
  return settled?.scope === scope ? settled.range : null;
}

function useTrackCatalog(
  ownerSubject: string,
  accessToken: string,
  selectedCollectorIds: string[] | null,
) {
  const query = useTracksQuery(ownerSubject, accessToken);
  const all = useMemo(() => query.data?.tracks ?? [], [query.data?.tracks]);
  const selected = useMemo(
    () =>
      selectedCollectorIds === null
        ? all
        : all.filter((track) => selectedCollectorIds.includes(track.collectorId)),
    [all, selectedCollectorIds],
  );
  const collectors = useMemo(
    () =>
      Array.from(
        new Map(
          all.map((track) => [
            track.collectorId,
            track.collectorDisplayName || track.collectorTarget,
          ]),
        ),
      ),
    [all],
  );
  return { query, all, selected, collectors };
}

function isZoomed(bounds: TimeRange | null, range: TimeRange | null) {
  return Boolean(range && bounds && (range.start !== bounds.start || range.end !== bounds.end));
}

function densityMessage(enabled: boolean, failed: boolean, pending: boolean) {
  if (!enabled) return null;
  if (failed) return "密度读取失败";
  return pending ? "正在读取密度…" : null;
}

function addDensityLayers(
  lanes: ReplayLane[],
  layers: ReturnType<typeof usePointDensityTiles>["layers"],
  range: TimeRange | null,
) {
  return lanes.map((lane) => ({
    ...lane,
    detailCounts:
      lane.track.timeMode === "point" && range
        ? cachedDensityLayers(layers, lane.track.id, range)
        : [],
  }));
}

function firstActivityTime(lanes: ReplayLane[]): number | null {
  let first: number | null = null;
  for (const lane of lanes) {
    const starts = lane.counts
      ? lane.counts.buckets.filter((bucket) => bucket.count > 0).map((bucket) => bucket.startedAt)
      : lane.records.map((record) => record.startedAt);
    for (const start of starts) {
      const at = Date.parse(start);
      if (Number.isFinite(at) && (first === null || at < first)) first = at;
    }
  }
  return first;
}

function sameRange(left: TimeRange | null, right: TimeRange | null) {
  return left?.start === right?.start && left?.end === right?.end;
}

function selectPointTracks(tracks: TrackSummary[]) {
  return tracks.filter((track) => track.timeMode === "point");
}

function useReplayDensity(
  ownerSubject: string,
  accessToken: string,
  tracks: TrackSummary[],
  bounds: TimeRange | null,
  range: TimeRange | null,
  scope: string,
) {
  const pointTracks = useMemo(() => selectPointTracks(tracks), [tracks]);
  const settled = useSettledRange(scope, range);
  const zoomed = isZoomed(bounds, range);
  const settledMatches = sameRange(settled, range);
  const query = usePointDensityTiles(
    ownerSubject,
    accessToken,
    pointTracks,
    bounds,
    zoomed ? settled : null,
    settled ? densityBucketSeconds(settled) : 900,
  );
  const enabled = zoomed && pointTracks.length > 0;
  return {
    query,
    status: densityMessage(enabled, query.isError, !settledMatches || query.isPending),
    failed: enabled && settledMatches && query.isError,
  };
}

function useReplayDetail(
  ownerSubject: string,
  accessToken: string,
  tracks: TrackSummary[],
  detail: ReturnType<typeof useReplaySelection>["detail"],
) {
  const track = tracks.find((candidate) => candidate.id === detail?.trackId) ?? null;
  const query = useRecordsQuery(
    ownerSubject,
    accessToken,
    track?.id ?? null,
    detail?.from ?? "",
    detail?.to ?? "",
  );
  return { track, query };
}

function useReplayRefresh(
  tracksQuery: ReturnType<typeof useTracksQuery>,
  replayQuery: ReturnType<typeof useReplayWindowQuery>,
  densityQuery: ReturnType<typeof usePointDensityTiles>,
  detailQuery: ReturnType<typeof useRecordsQuery>,
  hasDetail: boolean,
) {
  const [refreshing, setRefreshing] = useState(false);
  async function refresh() {
    setRefreshing(true);
    try {
      const detailRefresh = hasDetail ? detailQuery.refetch() : Promise.resolve();
      await Promise.all([
        tracksQuery.refetch(),
        replayQuery.refetch(),
        densityQuery.invalidate(),
        detailRefresh,
      ]);
    } finally {
      setRefreshing(false);
    }
  }
  return {
    refresh,
    fetching:
      refreshing ||
      tracksQuery.isFetching ||
      replayQuery.isFetching ||
      densityQuery.isFetching ||
      detailQuery.isFetching,
  };
}

export function useReplayData(
  ownerSubject: string,
  accessToken: string,
  selection: ReturnType<typeof useReplaySelection>,
) {
  const { from, to, bounds, range, selectedCollectorIds, detail } = selection;
  const catalog = useTrackCatalog(ownerSubject, accessToken, selectedCollectorIds);
  const replayQuery = useReplayWindowQuery(
    ownerSubject,
    accessToken,
    catalog.selected,
    from,
    to,
    bounds ? densityBucketSeconds(bounds) : 900,
  );
  // Undefined means incomplete; null means the complete window has no activity.
  const windowReady =
    selection.needsInitialRange &&
    !catalog.query.isPending &&
    !catalog.query.isError &&
    replayQuery.pending.length === 0 &&
    replayQuery.failures.length === 0;
  const firstActivityAt = useMemo(
    () => (windowReady ? firstActivityTime(replayQuery.data) : undefined),
    [windowReady, replayQuery.data],
  );
  const density = useReplayDensity(
    ownerSubject,
    accessToken,
    catalog.selected,
    bounds,
    range,
    `${ownerSubject}/${from}/${to}`,
  );
  const lanes = useMemo(
    () => addDensityLayers(replayQuery.data, density.query.layers, range),
    [replayQuery.data, density.query.layers, range],
  );
  const detailData = useReplayDetail(ownerSubject, accessToken, catalog.selected, detail);
  const refresh = useReplayRefresh(
    catalog.query,
    replayQuery,
    density.query,
    detailData.query,
    detailData.track !== null,
  );
  return {
    firstActivityAt,
    tracksQuery: catalog.query,
    allTracks: catalog.all,
    tracks: catalog.selected,
    collectors: catalog.collectors,
    replayQuery,
    densityQuery: density.query,
    densityStatus: density.status,
    densityFailed: density.failed,
    lanes,
    detailTrack: detailData.track,
    detailQuery: detailData.query,
    ...refresh,
  };
}
