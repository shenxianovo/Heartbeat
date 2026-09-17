import { describe, expect, it } from "vitest";
import type { PointCountsResponse } from "@/api/types";
import { cachedDensityLayers, densityTiles } from "./densityTiles";

const day = { start: Date.parse("2026-09-14T00:00:00Z"), end: Date.parse("2026-09-15T00:00:00Z") };
const iso = (at: number) => new Date(at).toISOString();
function counts(start: number, end: number, bucketSeconds: number): PointCountsResponse {
  return {
    track: {
      id: "point",
      collectorId: "collector",
      type: "example.point",
      version: 1,
      timeMode: "point",
      endMode: null,
    },
    from: iso(start),
    to: iso(end),
    bucketSeconds,
    buckets: [
      { index: 0, startedAt: iso(start), endedAt: iso(start + bucketSeconds * 1000), count: 4 },
    ],
  };
}

describe("density tile reuse", () => {
  it("keeps the same hour tile across small pans at one resolution", () => {
    const first = densityTiles(
      { start: day.start + 10 * 60_000, end: day.start + 20 * 60_000 },
      day,
      1,
    );
    const panned = densityTiles(
      { start: day.start + 11 * 60_000, end: day.start + 21 * 60_000 },
      day,
      1,
    );
    expect(panned).toEqual(first);
    expect(first).toEqual([{ start: day.start, end: day.start + 3_600_000 }]);
  });

  it("keeps cached fine data available across resolution changes", () => {
    const fine = counts(day.start + 3_600_000, day.start + 7_200_000, 1);
    const layers = cachedDensityLayers([fine], "point", {
      start: Date.parse(fine.from),
      end: Date.parse(fine.to),
    });
    expect(layers).toEqual([fine]);
    expect(cachedDensityLayers([fine], "other", day)).toEqual([]);
    expect(
      cachedDensityLayers([fine], "point", { start: day.start, end: day.start + 60_000 }),
    ).toEqual([]);
  });
});
