import type { PointCountsResponse } from "@/api/types";
import { densityBucketSeconds, type TimeRange } from "./timeRange";

export interface DensitySample {
  start: number;
  end: number;
  /** Events attributed to this sample's span. Fractional when a coarser layer is stretched over it. */
  count: number;
  /** False when no layer observed this span, which must break the curve instead of reading as zero. */
  covered: boolean;
}

export interface DensitySeries {
  /** Sample width in milliseconds. Cells are aligned to absolute epoch multiples so panning does not reflow them. */
  step: number;
  /** Finest bucket width backing this window, in seconds. Drops as refined tiles arrive. */
  sourceSeconds: number;
  samples: DensitySample[];
  peak: number;
}

interface DensityLayer {
  bucketSeconds: number;
  from: number;
  to: number;
  buckets: { start: number; end: number; count: number }[];
}

function toLayers(base: PointCountsResponse | null, detail: PointCountsResponse[]): DensityLayer[] {
  return [...(base ? [base] : []), ...detail]
    .map((counts) => ({
      bucketSeconds: counts.bucketSeconds,
      from: Date.parse(counts.from),
      to: Date.parse(counts.to),
      buckets: counts.buckets
        .map((bucket) => ({
          start: Date.parse(bucket.startedAt),
          end: Date.parse(bucket.endedAt),
          count: bucket.count,
        }))
        .filter((bucket) => bucket.end > bucket.start)
        .sort((left, right) => left.start - right.start),
    }))
    .filter((layer) => layer.to > layer.from && layer.bucketSeconds > 0)
    .sort(
      (left, right) =>
        left.bucketSeconds - right.bucketSeconds ||
        // At equal resolution the narrower window is the refined tile, so it wins over the
        // full-window base layer instead of the order the caller happened to pass them in.
        left.to - left.from - (right.to - right.from) ||
        left.from - right.from,
    );
}

function firstBucketEndingAfter(buckets: DensityLayer["buckets"], at: number): number {
  let low = 0;
  let high = buckets.length;
  while (low < high) {
    const middle = (low + high) >> 1;
    if (buckets[middle]!.end <= at) low = middle + 1;
    else high = middle;
  }
  return low;
}

/**
 * Splits one sample across the layers that observed it, preferring the finest layer for every
 * sub-span. Counts are apportioned by overlap, so a coarse bucket stretched over several samples
 * and a burst of fine buckets folded into one sample both preserve the event total.
 */
function sampleSpan(span: TimeRange, layers: DensityLayer[]) {
  let pending: TimeRange[] = [span];
  let count = 0;
  let covered = false;
  for (const layer of layers) {
    if (!pending.length) break;
    const rest: TimeRange[] = [];
    for (const piece of pending) {
      const start = Math.max(piece.start, layer.from);
      const end = Math.min(piece.end, layer.to);
      if (end <= start) {
        rest.push(piece);
        continue;
      }
      covered = true;
      for (
        let index = firstBucketEndingAfter(layer.buckets, start);
        index < layer.buckets.length;
        index++
      ) {
        const bucket = layer.buckets[index]!;
        if (bucket.start >= end) break;
        const from = Math.max(bucket.start, start);
        const to = Math.min(bucket.end, end);
        if (to > from) count += bucket.count * ((to - from) / (bucket.end - bucket.start));
      }
      if (start > piece.start) rest.push({ start: piece.start, end: start });
      if (end < piece.end) rest.push({ start: end, end: piece.end });
    }
    pending = rest;
  }
  return { count, covered };
}

/**
 * Turns the sparse per-bucket counts the API returns into a gap-free sample grid over the visible
 * range. The API omits empty buckets, so a renderer that walks the response directly can only draw
 * disconnected shapes; densifying here is what makes a single continuous curve possible.
 */
export function densitySeries(
  base: PointCountsResponse | null,
  detail: PointCountsResponse[],
  range: TimeRange,
): DensitySeries | null {
  const layers = toLayers(base, detail).filter(
    (layer) => layer.to > range.start && layer.from < range.end,
  );
  if (!layers.length || range.end <= range.start) return null;

  const step = densityBucketSeconds(range) * 1000;
  const first = Math.floor(range.start / step) * step;
  const samples: DensitySample[] = [];
  let peak = 0;
  for (let start = first; start < range.end; start += step) {
    const span = { start, end: start + step };
    const { count, covered } = sampleSpan(span, layers);
    samples.push({ ...span, count, covered });
    if (covered && count > peak) peak = count;
  }
  return {
    step,
    sourceSeconds: Math.min(...layers.map((layer) => layer.bucketSeconds)),
    samples,
    peak,
  };
}

/**
 * Square root keeps zero at zero while lifting quiet stretches into view. Input density spans
 * orders of magnitude, so a linear axis flattens everything but the busiest spikes at lane height.
 */
export function densityHeight(count: number, peak: number): number {
  if (peak <= 0 || count <= 0) return 0;
  return Math.min(1, Math.sqrt(count / peak));
}

export interface DensityBucket {
  start: number;
  end: number;
  count: number;
}

/**
 * The buckets a reader can drill into, finest layer first, coarser layers only where no finer layer
 * observed the span. Selection has to snap to these real bucket edges rather than to the drawing
 * grid: sample counts are apportioned by overlap, so a sample can carry a positive fraction of a
 * neighbouring bucket and hand back a window holding no records at all.
 *
 * `minBucketSeconds` keeps hit targets no finer than what is drawn. A tile cached from an earlier
 * zoom stays in the layer set, and a one-second bucket is impossible to click once the window is
 * back to a whole day — worse, since a finer layer masks the coarser one over its window, that
 * stale tile would blank out an hour of otherwise selectable buckets.
 */
export function densityBuckets(
  base: PointCountsResponse | null,
  detail: PointCountsResponse[],
  range: TimeRange,
  minBucketSeconds = 0,
): DensityBucket[] {
  const layers = toLayers(base, detail).filter(
    (layer) =>
      layer.to > range.start && layer.from < range.end && layer.bucketSeconds >= minBucketSeconds,
  );
  const claimed: TimeRange[] = [];
  const buckets: DensityBucket[] = [];
  for (const layer of layers) {
    for (const bucket of layer.buckets) {
      if (bucket.count <= 0 || bucket.end <= range.start || bucket.start >= range.end) continue;
      const middle = (bucket.start + bucket.end) / 2;
      if (claimed.some((window) => middle >= window.start && middle < window.end)) continue;
      buckets.push({ ...bucket });
    }
    claimed.push({ start: layer.from, end: layer.to });
  }
  return buckets.sort((left, right) => left.start - right.start);
}
