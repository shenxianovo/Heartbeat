import { useInfiniteQuery, useQuery } from "@tanstack/react-query";

import { fetchRecords, fetchTracks } from "@/api/client";

export const queryKeys = {
  tracks: (ownerSubject: string) => ["owner", ownerSubject, "tracks"] as const,
  records: (ownerSubject: string, trackId: string, from: string, to: string) =>
    ["owner", ownerSubject, "records", trackId, from, to] as const,
};

export function useTracksQuery(ownerSubject: string, accessToken: string) {
  return useQuery({
    queryKey: queryKeys.tracks(ownerSubject),
    queryFn: ({ signal }) => fetchTracks(accessToken, signal),
    enabled: Boolean(accessToken),
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
