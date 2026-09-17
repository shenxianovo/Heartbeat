import { describe, expect, it } from "vitest";
import { CURVE_HEIGHT, CURVE_WIDTH, densityGeometry } from "./densityGeometry";
import type { DensitySample, DensitySeries } from "./densitySeries";

const start = Date.parse("2026-09-14T01:00:00Z");
const step = 60_000;
const range = { start, end: start + 10 * step };

function sample(index: number, count: number, covered = true): DensitySample {
  return { start: start + index * step, end: start + (index + 1) * step, count, covered };
}

function series(samples: DensitySample[]): DensitySeries {
  return {
    step,
    sourceSeconds: 60,
    samples,
    peak: Math.max(0, ...samples.filter((item) => item.covered).map((item) => item.count)),
  };
}

const numbers = (path: string) => path.match(/-?\d+(\.\d+)?/g)!.map(Number);
const heights = (path: string) => numbers(path).filter((_, index) => index % 2 === 1);

describe("density geometry", () => {
  it("draws one segment for a continuous run and anchors it to the run edges", () => {
    const samples = [0, 1, 2, 3].map((index) => sample(index, index + 1));
    const geometry = densityGeometry(series(samples), range);

    expect(geometry.segments).toHaveLength(1);
    expect(geometry.segments[0]!.line).toMatch(/^M /);
    expect(geometry.segments[0]!.area).toMatch(/Z$/);
    // The fill starts at the run's leading edge and ends at its trailing edge, not half a sample in.
    expect(numbers(geometry.segments[0]!.line)[0]).toBeCloseTo(geometry.at(samples[0]!.start));
  });

  it("breaks the curve where no layer observed the span", () => {
    const geometry = densityGeometry(
      series([sample(0, 3), sample(1, 4), sample(2, 0, false), sample(3, 5), sample(4, 6)]),
      range,
    );

    expect(geometry.segments).toHaveLength(2);
    expect(geometry.segments.map((segment) => segment.key)).toEqual([0, 1]);
  });

  it("drops runs too short to draw rather than emitting a stray point", () => {
    const geometry = densityGeometry(
      series([sample(0, 3), sample(1, 0, false), sample(2, 4)]),
      range,
    );

    expect(geometry.segments).toEqual([]);
  });

  it("maps the visible range across the full viewBox width", () => {
    const geometry = densityGeometry(series([sample(0, 1), sample(1, 2)]), range);

    expect(geometry.at(range.start)).toBe(0);
    expect(geometry.at(range.end)).toBe(CURVE_WIDTH);
    expect(geometry.at((range.start + range.end) / 2)).toBeCloseTo(CURVE_WIDTH / 2);
  });

  it("puts the peak near the lane ceiling and silence on the baseline", () => {
    const samples = [sample(0, 0), sample(1, 100)];
    const geometry = densityGeometry(series(samples), range);

    const quiet = geometry.heightAt(samples[0]!.start + 1);
    const busy = geometry.heightAt(samples[1]!.start + 1);
    expect(quiet).toBeGreaterThan(busy);
    expect(quiet).toBeLessThan(CURVE_HEIGHT);
    expect(busy).toBeGreaterThan(0);
    // The drawn curve agrees with the probe, so hover markers land on the line.
    expect(heights(geometry.segments[0]!.line)).toContain(busy);
  });

  it("measures against the shared ceiling so a quiet lane reads as quiet", () => {
    const quiet = series([sample(0, 10), sample(1, 20)]);
    const alone = densityGeometry(quiet, range);
    const beside = densityGeometry(quiet, range, 200);

    expect(alone.scalePeak).toBe(20);
    expect(beside.scalePeak).toBe(200);
    // Same counts, but drawn against a busier neighbour they sit much closer to the floor.
    expect(beside.heightAt(range.start + step + 1)).toBeGreaterThan(
      alone.heightAt(range.start + step + 1),
    );
  });

  it("keeps its own peak as the ceiling when it is the tallest lane", () => {
    const geometry = densityGeometry(series([sample(0, 50)]), range, 10);

    expect(geometry.scalePeak).toBe(50);
  });

  it("reads out the drawn sample, and nothing where the curve is broken", () => {
    const geometry = densityGeometry(
      series([sample(0, 4), sample(1, 0), sample(2, 0, false)]),
      range,
    );

    expect(geometry.readingAt(range.start + 1)!.count).toBe(4);
    // Observed and empty is a real reading; it is what the flat stretch of curve means.
    expect(geometry.readingAt(range.start + step + 1)!.count).toBe(0);
    expect(geometry.readingAt(range.start + 2 * step + 1)).toBeNull();
    expect(geometry.readingAt(range.end + step)).toBeNull();
  });

  it("reads as an empty flat lane when there is no series to draw", () => {
    const geometry = densityGeometry(null, range);

    expect(geometry.segments).toEqual([]);
    expect(geometry.step).toBe(0);
    expect(geometry.sourceSeconds).toBe(0);
    expect(geometry.peak).toBe(0);
    expect(geometry.heightAt(range.start)).toBe(geometry.heightAt(range.end));
  });

  it("survives a collapsed range instead of dividing by zero", () => {
    const flat = { start, end: start };
    const geometry = densityGeometry(series([sample(0, 1), sample(1, 2)]), flat);

    expect(Number.isFinite(geometry.at(start))).toBe(true);
  });
});
