import { useInfiniteQuery, useQuery } from "@tanstack/react-query";

import { fetchAllRecords, fetchPointCounts, fetchRecords, fetchTracks } from "@/api/client";
import type { ReplayLane, TrackSummary } from "@/api/types";

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
};

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
