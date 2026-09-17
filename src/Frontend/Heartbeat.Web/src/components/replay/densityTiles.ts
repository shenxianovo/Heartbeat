import type { PointCountsResponse } from "@/api/types";
import type { TimeRange } from "./timeRange";

export function densityTiles(range: TimeRange, bounds: TimeRange, bucketSeconds: number) {
  const tileMs = (bucketSeconds < 60 ? 1 : 6) * 3_600_000;
  const first = Math.max(0, Math.floor((range.start - bounds.start) / tileMs));
  const last = Math.min(
    Math.ceil((bounds.end - bounds.start) / tileMs) - 1,
    Math.floor((range.end - 1 - bounds.start) / tileMs),
  );
  return Array.from({ length: Math.max(0, last - first + 1) }, (_, offset) => ({
    start: bounds.start + (first + offset) * tileMs,
    end: Math.min(bounds.end, bounds.start + (first + offset + 1) * tileMs),
  }));
}

export function cachedDensityLayers(
  data: PointCountsResponse[],
  trackId: string,
  range: TimeRange,
) {
  return data
    .filter(
      (counts) =>
        counts.track.id === trackId &&
        Date.parse(counts.from) < range.end &&
        Date.parse(counts.to) > range.start,
    )
    .sort((a, b) => b.bucketSeconds - a.bucketSeconds);
}
