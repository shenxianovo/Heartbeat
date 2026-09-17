import { useInfiniteQuery, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";

import { fetchAllRecords, fetchPointCounts, fetchRecords, fetchTracks } from "@/api/client";
import type { PointCountsResponse, ReplayLane, TrackSummary } from "@/api/types";
import { densityTiles } from "@/components/replay/densityTiles";
import type { TimeRange } from "@/components/replay/timeRange";

export const queryKeys = {
  tracks: (ownerSubject: string) => ["owner", ownerSubject, "tracks"] as const,
  records: (ownerSubject: string, trackId: string, from: string, to: string) =>
    ["owner", ownerSubject, "records", trackId, from, to] as const,
  replayWindow: (
    ownerSubject: string,
    trackIds: string[],
    from: string,
    to: string,
    bucketSeconds: number,
  ) => ["owner", ownerSubject, "replay-window", trackIds, from, to, bucketSeconds] as const,
  densityTiles: (ownerSubject: string, from: string, to: string, trackIds: string[]) =>
    ["owner", ownerSubject, "density-tiles", from, to, trackIds] as const,
};

export function usePointDensityTiles(
  ownerSubject: string,
  accessToken: string,
  tracks: TrackSummary[],
  bounds: TimeRange | null,
  range: TimeRange | null,
  bucketSeconds: number,
) {
  const client = useQueryClient();
  const from = bounds ? new Date(bounds.start).toISOString() : "";
  const to = bounds ? new Date(bounds.end).toISOString() : "";
  const trackIds = tracks.map((track) => track.id);
  const prefix = queryKeys.densityTiles(ownerSubject, from, to, trackIds);
  const tiles = bounds && range ? densityTiles(range, bounds, bucketSeconds) : [];
  const queries = useQueries({
    queries: tiles.map((tile) => ({
      queryKey: [...prefix, bucketSeconds, tile.start, tile.end],
      queryFn: ({ signal }: { signal: AbortSignal }): Promise<PointCountsResponse[]> =>
        Promise.all(
          tracks.map((track) =>
            fetchPointCounts(
              accessToken,
              track.id,
              new Date(tile.start).toISOString(),
              new Date(tile.end).toISOString(),
              bucketSeconds,
              signal,
            ),
          ),
        ),
      enabled: Boolean(accessToken && tracks.length && bounds && range),
      staleTime: Infinity,
      gcTime: 30 * 60_000,
    })),
  });
  const cached = client.getQueriesData<PointCountsResponse[]>({ queryKey: prefix });
  return {
    layers: cached.flatMap(([, data]) => data ?? []),
    isError: queries.some((query) => query.isError),
    isPending: queries.some((query) => query.isPending),
    refetch: () => Promise.all(queries.map((query) => query.refetch())),
    invalidate: () => client.invalidateQueries({ queryKey: prefix }),
  };
}

export function useTracksQuery(ownerSubject: string, accessToken: string) {
  return useQuery({
    queryKey: queryKeys.tracks(ownerSubject),
    queryFn: ({ signal }) => fetchTracks(accessToken, signal),
    enabled: Boolean(accessToken),
  });
}

export function useReplayWindowQuery(
  ownerSubject: string,
  accessToken: string,
  tracks: TrackSummary[],
  from: string,
  to: string,
  bucketSeconds: number,
) {
  const trackIds = tracks.map((track) => track.id);
  return useQuery({
    queryKey: queryKeys.replayWindow(ownerSubject, trackIds, from, to, bucketSeconds),
    queryFn: async ({ signal }): Promise<ReplayLane[]> =>
      Promise.all(
        tracks.map(async (track) =>
          track.timeMode === "point"
            ? {
                track,
                records: [],
                counts: await fetchPointCounts(
                  accessToken,
                  track.id,
                  from,
                  to,
                  bucketSeconds,
                  signal,
                ),
              }
            : {
                track,
                records: (
                  await fetchAllRecords(accessToken, { trackId: track.id, from, to }, signal)
                ).records,
                counts: null,
              },
        ),
      ),
    enabled: Boolean(accessToken && tracks.length && from && to),
  });
}

export function useRecordsQuery(
  ownerSubject: string,
  accessToken: string,
  trackId: string | null,
  from: string,
  to: string,
) {
  return useInfiniteQuery({
    queryKey: queryKeys.records(ownerSubject, trackId ?? "", from, to),
    queryFn: ({ pageParam, signal }) =>
      fetchRecords(
        accessToken,
        { trackId: trackId ?? "", from, to, cursor: pageParam, limit: 100 },
        signal,
      ),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    enabled: Boolean(accessToken && trackId),
  });
}
