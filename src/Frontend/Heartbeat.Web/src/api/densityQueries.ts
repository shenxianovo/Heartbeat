import { useMemo, useSyncExternalStore } from "react";
import { matchQuery, useQueries, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { fetchPointCounts } from "./client";
import { queryKeys } from "./queryKeys";
import type { PointCountsResponse, TrackSummary } from "./types";
import { densityTiles } from "@/components/replay/densityTiles";
import type { TimeRange } from "@/components/replay/timeRange";

const emptyLayers: PointCountsResponse[] = [];

// QueryClient remains the only data owner. The snapshot is stable until layer data changes;
// unrelated queries and observer notifications must not rebuild the timeline on every frame.
function densityStore(client: QueryClient, prefix: readonly string[]) {
  let snapshot = emptyLayers;
  const filters = { queryKey: prefix };
  return {
    subscribe: (notify: () => void) =>
      client.getQueryCache().subscribe((event) => {
        if (matchQuery(filters, event.query)) notify();
      }),
    getSnapshot: () => {
      const next = client
        .getQueryCache()
        .findAll(filters)
        .flatMap((query) => {
          const data = query.state.data as PointCountsResponse | undefined;
          return data && !query.state.isInvalidated ? [data] : [];
        });
      if (next.length !== snapshot.length || next.some((data, index) => data !== snapshot[index])) {
        snapshot = next;
      }
      return snapshot;
    },
  };
}

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
  const prefix = useMemo(
    () => queryKeys.densityWindow(ownerSubject, from, to),
    [ownerSubject, from, to],
  );
  const tiles = bounds && range ? densityTiles(range, bounds, bucketSeconds) : [];
  const queries = useQueries({
    queries: tracks.flatMap((track) =>
      tiles.map((tile) => ({
        queryKey: [...prefix, track.id, bucketSeconds, tile.start, tile.end],
        queryFn: ({ signal }: { signal: AbortSignal }) =>
          fetchPointCounts(
            accessToken,
            track.id,
            new Date(tile.start).toISOString(),
            new Date(tile.end).toISOString(),
            bucketSeconds,
            signal,
          ),
        enabled: Boolean(accessToken),
        staleTime: Infinity,
        gcTime: 30 * 60_000,
      })),
    ),
  });
  const store = useMemo(() => densityStore(client, prefix), [client, prefix]);
  const cached = useSyncExternalStore(store.subscribe, store.getSnapshot, () => emptyLayers);
  const layers = useMemo(
    () => cached.filter((data) => tracks.some((track) => track.id === data.track.id)),
    [cached, tracks],
  );
  return {
    layers,
    isError: queries.some((query) => query.isError),
    isPending: queries.some((query) => query.isPending),
    isFetching: queries.some((query) => query.isFetching),
    refetch: () => Promise.all(queries.map((query) => query.refetch())),
    invalidate: async () => {
      // Cancel older in-flight snapshots before invalidating every cached granularity,
      // including inactive tiles and temporarily deselected Tracks in this window.
      await client.cancelQueries({ queryKey: prefix });
      await client.invalidateQueries({ queryKey: prefix });
    },
  };
}
