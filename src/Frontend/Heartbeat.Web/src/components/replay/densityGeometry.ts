import { densityHeight, type DensitySample, type DensitySeries } from "./densitySeries";
import { smoothArea, smoothLine, type CurvePoint } from "./smoothCurve";
import type { TimeRange } from "./timeRange";

/** The curve is drawn once in this viewBox and stretched to the lane, so all figures here are viewBox units. */
export const CURVE_WIDTH = 1000;
export const CURVE_HEIGHT = 40;
/** Zero sits just above the lane floor, the busiest sample just below its ceiling. */
const BASELINE = 37;
const CEILING = 4;

export interface CurveSegment {
  key: number;
  /** The shape closed down to the baseline, for the gradient fill. */
  area: string;
  /** The same top edge on its own, for the stroke. */
  line: string;
}

/** Everything the view needs to draw and probe one lane's curve, with no time-to-pixel maths left in the component. */
export interface DensityGeometry {
  /** Sample width in milliseconds, 0 when nothing is drawn. */
  step: number;
  /** This lane's own busiest sample. */
  peak: number;
  /** The value drawn at the lane ceiling. Shared across lanes so their heights can be compared. */
  scalePeak: number;
  /** Finest bucket width backing the drawing, in seconds. */
  sourceSeconds: number;
  segments: CurveSegment[];
  /** Maps an instant to its x position. */
  at(value: number): number;
  /** Reads the drawn height at an instant, so markers sit on the curve rather than on raw counts. */
  heightAt(value: number): number;
  /** The drawn sample at an instant, or null where nothing was observed and the curve is broken. */
  readingAt(value: number): DensitySample | null;
}

function sampleAt(samples: DensitySample[], value: number): DensitySample | null {
  const sample = samples.find((item) => value >= item.start && value < item.end) ?? null;
  return sample?.covered ? sample : null;
}

/**
 * Groups the sample grid into runs of observed samples. A break means no layer covered that span,
 * which must interrupt the curve rather than read as a stretch of zero activity. Each run is
 * anchored to its outer sample edges so the fill reaches the edge of the span it describes instead
 * of stopping half a sample short at the first and last sample centre.
 */
function runs(
  samples: DensitySample[],
  at: (value: number) => number,
  height: (count: number) => number,
): CurveSegment[] {
  const segments: CurveSegment[] = [];
  let run: DensitySample[] = [];
  const flush = () => {
    if (run.length >= 2) {
      const first = run[0]!;
      const last = run[run.length - 1]!;
      const points: CurvePoint[] = [
        { x: at(first.start), y: height(first.count) },
        ...run.map((sample) => ({
          x: at((sample.start + sample.end) / 2),
          y: height(sample.count),
        })),
        { x: at(last.end), y: height(last.count) },
      ];
      segments.push({
        key: segments.length,
        area: smoothArea(points, BASELINE),
        line: smoothLine(points),
      });
    }
    run = [];
  };
  for (const sample of samples) {
    if (sample.covered) run.push(sample);
    else flush();
  }
  flush();
  return segments;
}

/**
 * Projects a sample grid onto the lane. Safe to call with no series: it yields an empty, flat geometry.
 * `scalePeak` is the count drawn at the ceiling; pass the peak shared by every lane to make their
 * heights comparable. It falls back to this lane's own peak, which is the single-lane case.
 */
export function densityGeometry(
  series: DensitySeries | null,
  range: TimeRange,
  scalePeak = 0,
): DensityGeometry {
  const span = Math.max(1, range.end - range.start);
  const at = (value: number) => ((value - range.start) / span) * CURVE_WIDTH;
  const samples = series?.samples ?? [];
  const peak = series?.peak ?? 0;
  const ceiling = Math.max(scalePeak, peak);
  const height = (count: number) => BASELINE - densityHeight(count, ceiling) * (BASELINE - CEILING);
  return {
    step: series?.step ?? 0,
    peak,
    scalePeak: ceiling,
    sourceSeconds: series?.sourceSeconds ?? 0,
    segments: runs(samples, at, height),
    at,
    heightAt: (value: number) => height(sampleAt(samples, value)?.count ?? 0),
    readingAt: (value: number) => sampleAt(samples, value),
  };
}
