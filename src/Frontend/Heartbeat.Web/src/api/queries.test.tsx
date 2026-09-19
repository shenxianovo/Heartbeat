import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { PropsWithChildren } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { usePointDensityTiles } from "@/api/densityQueries";
import { fetchAllRecords, fetchPointCounts, fetchTracks, fetchRecords } from "@/api/client";
import { useReplayData } from "@/components/replay/useReplayData";
import { useReplaySelection } from "@/components/replay/useReplaySelection";
import { queryKeys } from "@/api/queryKeys";
import { useReplayWindowQuery } from "@/api/queries";
import type { PointCountsResponse, TrackSummary } from "@/api/types";

vi.mock("@/api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/client")>();
  return {
    ...original,
    fetchAllRecords: vi.fn(),
    fetchPointCounts: vi.fn(),
    fetchTracks: vi.fn(),
    fetchRecords: vi.fn(),
  };
});

const rangeTrack: TrackSummary = {
  id: "range-track",
  collectorId: "collector",
  collectorKey: "example",
  collectorTarget: "device",
  collectorDisplayName: "Device",
  type: "example.range",
  version: 1,
  timeMode: "range",
  endMode: "explicit",
  createdAt: "2026-09-18T00:00:00Z",
};
const failedTrack = { ...rangeTrack, id: "failed-track", type: "example.failed" };
const pointTrack = {
  ...rangeTrack,
  id: "point-track",
  type: "example.point",
  timeMode: "point" as const,
  endMode: null,
};
const bounds = {
  start: Date.parse("2026-09-18T00:00:00Z"),
  end: Date.parse("2026-09-19T00:00:00Z"),
};

function createHarness() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return {
    client,
    wrapper: ({ children }: PropsWithChildren) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    ),
  };
}

function counts(bucketSeconds: number): PointCountsResponse {
  return {
    track: pointTrack,
    from: new Date(bounds.start).toISOString(),
    to: new Date(bounds.end).toISOString(),
    bucketSeconds,
    buckets: [],
  };
}

afterEach(() => vi.resetAllMocks());

function observedCounts(
  track: TrackSummary,
  from: string,
  to: string,
  bucketSeconds: number,
  count: number,
): PointCountsResponse {
  return {
    track,
    from,
    to,
    bucketSeconds,
    buckets: [
      {
        index: 0,
        startedAt: from,
        endedAt: new Date(Date.parse(from) + bucketSeconds * 1000).toISOString(),
        count,
      },
    ],
  };
}

function renderReplay() {
  const harness = createHarness();
  vi.mocked(fetchTracks).mockResolvedValue({ tracks: [pointTrack] });
  const view = renderHook(
    () => {
      const selection = useReplaySelection();
      return { selection, data: useReplayData("owner", "token", selection) };
    },
    { wrapper: harness.wrapper },
  );
  return { ...harness, ...view };
}

describe("replay orchestration", () => {
  it("refresh replaces counts and invalidates inactive finer layers before zooming again", async () => {
    let count = 1;
    vi.mocked(fetchPointCounts).mockImplementation(async (_token, _id, from, to, seconds) =>
      observedCounts(pointTrack, from, to, seconds, count),
    );
    const { result, client } = renderReplay();
    await waitFor(() => expect(result.current.data.lanes).toHaveLength(1));
    const { from, to, bounds: selectedBounds } = result.current.selection;
    const prefix = queryKeys.densityWindow("owner", from, to);
    const oldKey = [...prefix, pointTrack.id, 1, selectedBounds!.start, selectedBounds!.end];
    act(() => client.setQueryData(oldKey, observedCounts(pointTrack, from, to, 1, 1)));
    await waitFor(() => expect(result.current.data.lanes[0]?.detailCounts).toHaveLength(1));

    count = 9;
    await act(() => result.current.data.refresh());
    expect(result.current.data.lanes[0]?.counts?.buckets[0]?.count).toBe(9);
    expect(result.current.data.lanes[0]?.detailCounts).toEqual([]);
    expect(client.getQueryState(oldKey)?.isInvalidated).toBe(true);

    act(() =>
      result.current.selection.setRange({
        start: selectedBounds!.start,
        end: selectedBounds!.start + 60_000,
      }),
    );
    await waitFor(() =>
      expect(result.current.data.lanes[0]?.detailCounts?.[0]?.buckets[0]?.count).toBe(9),
    );
  });

  it("date and source changes cannot expose a previous point detail", async () => {
    vi.mocked(fetchPointCounts).mockImplementation(async (_token, _id, from, to, seconds) =>
      observedCounts(pointTrack, from, to, seconds, 1),
    );
    vi.mocked(fetchRecords).mockResolvedValue({ track: pointTrack, records: [], nextCursor: null });
    const { result } = renderReplay();
    await waitFor(() => expect(result.current.data.lanes).toHaveLength(1));
    const { from, to } = result.current.selection;
    act(() => result.current.selection.setDetail({ trackId: pointTrack.id, from, to, count: 1 }));
    await waitFor(() => expect(result.current.data.detailQuery.isSuccess).toBe(true));
    act(() => result.current.selection.selectCollectors([]));
    expect(result.current.data.detailTrack).toBeNull();
    expect(result.current.data.lanes).toEqual([]);
    act(() => result.current.selection.selectCollectors(null));
    expect(result.current.data.detailTrack).toBeNull();
    act(() => result.current.selection.setDetail({ trackId: pointTrack.id, from, to, count: 1 }));
    act(() =>
      result.current.selection.chooseDate({ from: "2026-09-10T00:00", to: "2026-09-11T00:00" }),
    );
    expect(result.current.data.detailTrack).toBeNull();
    expect(result.current.data.lanes).toEqual([]);
    await waitFor(() =>
      expect(result.current.data.lanes[0]?.counts?.from).toBe(result.current.selection.from),
    );
  });

  it("keeps the refresh busy until active fine queries finish", async () => {
    vi.mocked(fetchPointCounts).mockImplementation(async (_token, _id, from, to, seconds) =>
      observedCounts(pointTrack, from, to, seconds, 1),
    );
    const { result } = renderReplay();
    await waitFor(() => expect(result.current.data.lanes).toHaveLength(1));
    const { bounds: selectedBounds } = result.current.selection;
    act(() =>
      result.current.selection.setRange({
        start: selectedBounds!.start,
        end: selectedBounds!.start + 60_000,
      }),
    );
    await waitFor(() => expect(result.current.data.densityQuery.layers).toHaveLength(1));
    let release!: () => void;
    const pending = new Promise<void>((resolve) => {
      release = resolve;
    });
    vi.mocked(fetchPointCounts).mockImplementation(async (_token, _id, from, to, seconds) => {
      if (seconds < 900) await pending;
      return observedCounts(pointTrack, from, to, seconds, 7);
    });
    let refresh!: Promise<void>;
    act(() => {
      refresh = result.current.data.refresh();
    });
    await waitFor(() => expect(result.current.data.densityQuery.layers).toEqual([]));
    expect(result.current.data.fetching).toBe(true);
    await act(async () => {
      release();
      await refresh;
    });
    await waitFor(() => expect(result.current.data.fetching).toBe(false));
    expect(result.current.data.densityQuery.layers[0]?.buckets[0]?.count).toBe(7);
  });
});

describe("replay Track queries", () => {
  it("keeps successful Track data when another Track fails", async () => {
    vi.mocked(fetchAllRecords).mockImplementation(async (_token, query) => {
      if (query.trackId === failedTrack.id) throw new Error("track unavailable");
      return { track: rangeTrack, records: [], nextCursor: null };
    });
    const { wrapper } = createHarness();
    const { result } = renderHook(
      () => useReplayWindowQuery("owner", "token", [rangeTrack, failedTrack], "from", "to", 900),
      { wrapper },
    );

    await waitFor(() => expect(result.current.failures).toHaveLength(1));
    expect(result.current.data.map((lane) => lane.track.id)).toEqual([rangeTrack.id]);
    expect(result.current.failures[0]?.track.id).toBe(failedTrack.id);
  });

  it("reuses an unchanged Track query when the selected set changes", async () => {
    vi.mocked(fetchAllRecords).mockResolvedValue({
      track: rangeTrack,
      records: [],
      nextCursor: null,
    });
    const { wrapper } = createHarness();
    const { result, rerender } = renderHook(
      ({ tracks }) => useReplayWindowQuery("owner", "token", tracks, "from", "to", 900),
      { wrapper, initialProps: { tracks: [rangeTrack] } },
    );
    await waitFor(() => expect(result.current.data).toHaveLength(1));

    rerender({ tracks: [rangeTrack, failedTrack] });
    await waitFor(() => expect(result.current.data).toHaveLength(2));
    expect(
      vi.mocked(fetchAllRecords).mock.calls.filter(([, query]) => query.trackId === rangeTrack.id),
    ).toHaveLength(1);
  });
});

describe("density cache subscription", () => {
  it("updates from QueryClient changes and hides invalidated fine layers", async () => {
    vi.mocked(fetchPointCounts).mockResolvedValue(counts(60));
    const { client, wrapper } = createHarness();
    const { result } = renderHook(
      () => usePointDensityTiles("owner", "token", [pointTrack], bounds, null, 60),
      { wrapper },
    );
    const prefix = queryKeys.densityWindow(
      "owner",
      new Date(bounds.start).toISOString(),
      new Date(bounds.end).toISOString(),
    );
    const fineKey = [...prefix, pointTrack.id, 60, bounds.start, bounds.end];

    act(() => client.setQueryData(fineKey, counts(60)));
    await waitFor(() =>
      expect(result.current.layers.map((layer) => layer.bucketSeconds)).toEqual([60]),
    );

    await act(() => client.invalidateQueries({ queryKey: prefix, refetchType: "none" }));
    await waitFor(() => expect(result.current.layers).toEqual([]));
  });
});
