import { describe, expect, it } from "vitest";
import type { PointCountBucket, PointCountsResponse } from "@/api/types";
import { densityBuckets, densityHeight, densitySeries } from "./densitySeries";

const iso = (at: number) => new Date(at).toISOString();
const hourStart = Date.parse("2026-09-14T01:00:00Z");
const hour = { start: hourStart, end: hourStart + 3_600_000 };
const day = { start: Date.parse("2026-09-14T00:00:00Z"), end: Date.parse("2026-09-15T00:00:00Z") };

function bucket(start: number, seconds: number, count: number): PointCountBucket {
  return { index: 0, startedAt: iso(start), endedAt: iso(start + seconds * 1000), count };
}

function counts(
  from: number,
  to: number,
  bucketSeconds: number,
  buckets: PointCountBucket[],
): PointCountsResponse {
  return {
    track: {
      id: "point",
      collectorId: "collector",
      type: "example.point",
      version: 1,
      timeMode: "point",
      endMode: null,
    },
    from: iso(from),
    to: iso(to),
    bucketSeconds,
    buckets,
  };
}

const sampleAt = (series: NonNullable<ReturnType<typeof densitySeries>>, start: number) =>
  series.samples.find((sample) => sample.start === start)!;

describe("density series", () => {
  it("fills the gaps the API omits so the whole window is one continuous run", () => {
    const base = counts(hour.start, hour.end, 30, [bucket(hour.start + 600_000, 30, 4)]);
    const series = densitySeries(base, [], hour)!;

    expect(series.step).toBe(30_000);
    expect(series.samples).toHaveLength(120);
    expect(series.samples.every((sample) => sample.covered)).toBe(true);
    expect(series.samples.filter((sample) => sample.count > 0)).toHaveLength(1);
    expect(sampleAt(series, hour.start + 600_000).count).toBe(4);
    expect(sampleAt(series, hour.start).count).toBe(0);
    expect(series.peak).toBe(4);
  });

  it("prefers the finest layer that observed a span and falls back to the coarse one elsewhere", () => {
    const base = counts(day.start, day.end, 900, [
      bucket(hour.start, 900, 900),
      bucket(hour.start + 1_800_000, 900, 300),
    ]);
    const fine = counts(hour.start, hour.start + 1_800_000, 30, [bucket(hour.start, 30, 5)]);
    const series = densitySeries(base, [fine], hour)!;

    expect(series.sourceSeconds).toBe(30);
    expect(sampleAt(series, hour.start).count).toBe(5);
    expect(sampleAt(series, hour.start + 30_000).count).toBe(0);
    expect(sampleAt(series, hour.start + 1_800_000).count).toBe(10);
  });

  it("preserves the event total when rebinning across resolutions", () => {
    const fine = counts(hour.start, hour.end, 1, [
      bucket(hour.start, 1, 1),
      bucket(hour.start + 1_000, 1, 2),
      bucket(hour.start + 2_000, 1, 3),
    ]);
    const folded = densitySeries(counts(day.start, day.end, 900, []), [fine], hour)!;
    expect(folded.sourceSeconds).toBe(1);
    expect(sampleAt(folded, hour.start).count).toBe(6);
    expect(sampleAt(folded, hour.start + 30_000).count).toBe(0);

    const coarse = counts(day.start, day.end, 900, [bucket(hour.start, 900, 900)]);
    const stretched = densitySeries(coarse, [], hour)!;
    const spread = stretched.samples
      .filter((sample) => sample.start < hour.start + 900_000)
      .reduce((total, sample) => total + sample.count, 0);
    expect(spread).toBeCloseTo(900);
  });

  it("prefers a refined tile over the base layer at the same resolution", () => {
    const base = counts(day.start, day.end, 900, [bucket(hour.start, 900, 900)]);
    const tile = counts(hour.start, hour.start + 900_000, 900, []);
    const series = densitySeries(base, [tile], hour)!;
    expect(sampleAt(series, hour.start).count).toBe(0);
    expect(sampleAt(series, hour.start).covered).toBe(true);
  });

  it("marks unobserved spans so the curve breaks instead of reading as zero", () => {
    const base = counts(hour.start, hour.start + 1_800_000, 30, [bucket(hour.start, 30, 2)]);
    const series = densitySeries(base, [], hour)!;

    expect(sampleAt(series, hour.start).covered).toBe(true);
    expect(sampleAt(series, hour.start + 1_800_000).covered).toBe(false);
    expect(series.peak).toBe(2);
  });

  it("has nothing to draw without any layer", () => {
    expect(densitySeries(null, [], hour)).toBeNull();
    expect(densitySeries(counts(day.end, day.end + 1000, 30, []), [], hour)).toBeNull();
  });

  it("scales height by square root so quiet stretches stay visible and zero stays flat", () => {
    expect(densityHeight(0, 100)).toBe(0);
    expect(densityHeight(100, 100)).toBe(1);
    expect(densityHeight(25, 100)).toBeCloseTo(0.5);
    expect(densityHeight(1, 100)).toBeCloseTo(0.1);
    expect(densityHeight(5, 0)).toBe(0);
  });
});

describe("density buckets", () => {
  it("offers the real bucket edges rather than the drawing grid", () => {
    const base = counts(day.start, day.end, 900, [bucket(hour.start, 900, 7)]);
    const series = densitySeries(base, [], hour)!;
    const [only] = densityBuckets(base, [], hour);

    // The drawing grid apportions the bucket across 30 samples, none of which is a safe drill-down
    // window, so selection must hand back the 900s bucket that actually holds the records.
    expect(series.step).toBe(30_000);
    expect(only).toEqual({ start: hour.start, end: hour.start + 900_000, count: 7 });
  });

  it("keeps counts whole and drops empty buckets", () => {
    const base = counts(day.start, day.end, 900, [
      bucket(hour.start, 900, 3),
      bucket(hour.start + 900_000, 900, 0),
    ]);
    expect(densityBuckets(base, [], hour).map((entry) => entry.count)).toEqual([3]);
  });

  it("replaces a coarse bucket with the fine buckets that refine it, in time order", () => {
    const base = counts(day.start, day.end, 900, [
      bucket(hour.start, 900, 90),
      bucket(hour.start + 1_800_000, 900, 40),
    ]);
    const fine = counts(hour.start, hour.start + 900_000, 30, [
      bucket(hour.start + 60_000, 30, 5),
      bucket(hour.start, 30, 2),
    ]);
    const result = densityBuckets(base, [fine], hour);

    expect(result.map((entry) => [entry.start - hour.start, entry.count])).toEqual([
      [0, 2],
      [60_000, 5],
      [1_800_000, 40],
    ]);
  });

  it("ignores buckets outside the visible range", () => {
    const base = counts(day.start, day.end, 900, [
      bucket(day.start, 900, 4),
      bucket(hour.start, 900, 6),
    ]);
    expect(densityBuckets(base, [], hour).map((entry) => entry.count)).toEqual([6]);
  });

  it("ignores tiles finer than the drawn resolution instead of letting them blank out the hour", () => {
    const base = counts(day.start, day.end, 900, [bucket(hour.start, 900, 6)]);
    // A one-second tile left over from an earlier zoom. It masks the coarse bucket over its whole
    // window, so honouring it at day resolution would leave that hour with a single unclickable
    // one-second target.
    const stale = counts(hour.start, hour.start + 3_600_000, 1, [
      bucket(hour.start + 60_000, 1, 1),
    ]);

    expect(densityBuckets(base, [stale], day, 0).map((entry) => entry.count)).toEqual([1]);
    expect(densityBuckets(base, [stale], day, 900).map((entry) => entry.count)).toEqual([6]);
    expect(densityBuckets(base, [stale], day, 900)[0]).toEqual({
      start: hour.start,
      end: hour.start + 900_000,
      count: 6,
    });
  });

  it("still honours a tile that matches the drawn resolution", () => {
    const base = counts(day.start, day.end, 900, [bucket(hour.start, 900, 6)]);
    const fresh = counts(hour.start, hour.start + 900_000, 30, [
      bucket(hour.start + 60_000, 30, 2),
    ]);
    expect(densityBuckets(base, [fresh], hour, 30).map((entry) => entry.count)).toEqual([2]);
  });
});
