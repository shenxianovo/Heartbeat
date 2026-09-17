import { describe, expect, it } from "vitest";
import type { PointCountBucket, PointCountsResponse, ReplayLane, TrackSummary } from "@/api/types";
import { densityScale } from "./densityScale";

const iso = (at: number) => new Date(at).toISOString();
const hourStart = Date.parse("2026-09-14T01:00:00Z");
const hour = { start: hourStart, end: hourStart + 3_600_000 };

function track(id: string, timeMode: "point" | "range"): TrackSummary {
  return {
    id,
    collectorId: `collector-${id}`,
    collectorKey: `key-${id}`,
    collectorTarget: `target-${id}`,
    collectorDisplayName: `设备 ${id}`,
    type: "desktop.input.event",
    version: 1,
    timeMode,
    endMode: null,
    createdAt: iso(hourStart),
  };
}

function counts(trackSummary: TrackSummary, buckets: PointCountBucket[]): PointCountsResponse {
  return {
    track: trackSummary,
    from: iso(hour.start),
    to: iso(hour.end),
    bucketSeconds: 30,
    buckets,
  };
}

function bucket(offset: number, count: number): PointCountBucket {
  return {
    index: 0,
    startedAt: iso(hour.start + offset),
    endedAt: iso(hour.start + offset + 30_000),
    count,
  };
}

function lane(trackSummary: TrackSummary, buckets: PointCountBucket[]): ReplayLane {
  return {
    track: trackSummary,
    records: [],
    counts: buckets.length ? counts(trackSummary, buckets) : null,
    detailCounts: [],
  };
}

describe("density scale", () => {
  it("draws every point lane against the busiest lane in view", () => {
    const busy = track("busy", "point");
    const quiet = track("quiet", "point");
    const scale = densityScale([lane(busy, [bucket(0, 900)]), lane(quiet, [bucket(0, 30)])], hour);

    expect(scale.peak).toBe(900);
    // Each lane keeps its own samples; only the ceiling they are measured against is shared.
    expect(scale.seriesFor(busy.id)!.peak).toBe(900);
    expect(scale.seriesFor(quiet.id)!.peak).toBe(30);
  });

  it("ignores range lanes, which carry no density", () => {
    const points = track("points", "point");
    const ranges = track("ranges", "range");
    const scale = densityScale([lane(points, [bucket(0, 12)]), lane(ranges, [])], hour);

    expect(scale.peak).toBe(12);
    expect(scale.seriesFor(ranges.id)).toBeNull();
  });

  it("reports nothing to draw for a lane it never saw", () => {
    const scale = densityScale([], hour);

    expect(scale.peak).toBe(0);
    expect(scale.seriesFor("missing")).toBeNull();
  });
});
