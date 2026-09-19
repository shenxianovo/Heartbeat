import { useInfiniteQuery, useQueries, useQuery } from "@tanstack/react-query";

import { fetchAllRecords, fetchPointCounts, fetchRecords, fetchTracks } from "@/api/client";
import type { ReplayLane, TrackSummary } from "@/api/types";
import { queryKeys } from "./queryKeys";

export function useTracksQuery(ownerSubject: string, accessToken: string) {
  return useQuery({
    queryKey: queryKeys.tracks(ownerSubject),
    queryFn: ({ signal }) => fetchTracks(accessToken, signal),
    enabled: Boolean(accessToken),
  });
}

async function fetchReplayLane(
  accessToken: string,
  track: TrackSummary,
  from: string,
  to: string,
  bucketSeconds: number,
  signal: AbortSignal,
): Promise<ReplayLane> {
  if (track.timeMode === "point") {
    const counts = await fetchPointCounts(accessToken, track.id, from, to, bucketSeconds, signal);
    return { track, records: [], counts };
  }
  const { records } = await fetchAllRecords(accessToken, { trackId: track.id, from, to }, signal);
  return { track, records, counts: null };
}

export function useReplayWindowQuery(
  ownerSubject: string,
  accessToken: string,
  tracks: TrackSummary[],
  from: string,
  to: string,
  bucketSeconds: number,
) {
  return useQueries({
    queries: tracks.map((track) => ({
      queryKey: queryKeys.replayTrack(
        ownerSubject,
        track.id,
        from,
        to,
        track.timeMode === "point" ? bucketSeconds : 0,
      ),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        fetchReplayLane(accessToken, track, from, to, bucketSeconds, signal),
      enabled: Boolean(accessToken && from && to),
    })),
    combine: (queries) => ({
      data: queries.flatMap((query, index) =>
        query.data ? [{ ...query.data, track: tracks[index]! }] : [],
      ),
      failures: queries.flatMap((query, index) =>
        query.error ? [{ track: tracks[index]!, error: query.error, retry: query.refetch }] : [],
      ),
      pending: queries.flatMap((query, index) => (query.isPending ? [tracks[index]!] : [])),
      isFetching: queries.some((query) => query.isFetching),
      refetch: () => Promise.all(queries.map((query) => query.refetch())),
    }),
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
    enabled: Boolean(accessToken && trackId && from && to),
  });
}
