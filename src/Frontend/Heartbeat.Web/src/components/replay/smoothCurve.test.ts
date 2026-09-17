import { describe, expect, it } from "vitest";
import { smoothArea, smoothLine, type CurvePoint } from "./smoothCurve";

function segments(path: string) {
  const numbers = path.match(/-?\d+(?:\.\d+)?/g)!.map(Number);
  const result: number[][] = [];
  let x = numbers[0]!;
  let y = numbers[1]!;
  for (let index = 2; index + 5 < numbers.length; index += 6) {
    result.push([x, y, ...numbers.slice(index, index + 6)]);
    x = numbers[index + 4]!;
    y = numbers[index + 5]!;
  }
  return result;
}

function heights(path: string) {
  return segments(path).flatMap((segment) => {
    const [, y0, , c1y, , c2y, , y1] = segment as unknown as number[];
    return Array.from({ length: 21 }, (_, step) => {
      const t = step / 20;
      const inverse = 1 - t;
      return (
        inverse ** 3 * y0! +
        3 * inverse ** 2 * t * c1y! +
        3 * inverse * t ** 2 * c2y! +
        t ** 3 * y1!
      );
    });
  });
}

const points = (values: number[]): CurvePoint[] => values.map((y, index) => ({ x: index * 10, y }));

describe("smooth curve", () => {
  it("draws every point as one M-rooted subpath", () => {
    const path = smoothLine(points([30, 10, 20]));
    expect(path.startsWith("M 0 30")).toBe(true);
    expect(path.match(/M /g)).toHaveLength(1);
    expect(path.match(/ C /g)).toHaveLength(2);
    expect(path.endsWith("20 20")).toBe(true);
  });

  it("never overshoots past a turning point, so a spike cannot dip below the baseline", () => {
    const spike = heights(smoothLine(points([37, 37, 6, 37, 37])));
    expect(Math.max(...spike)).toBeLessThanOrEqual(37.001);
    expect(Math.min(...spike)).toBeGreaterThanOrEqual(5.999);
  });

  it("keeps a flat run flat", () => {
    for (const y of heights(smoothLine(points([37, 37, 37])))) expect(y).toBeCloseTo(37);
  });

  it("stays monotone across a rising run", () => {
    const rising = heights(smoothLine(points([0, 10, 20, 30])));
    expect(rising.every((y, index) => index === 0 || y >= rising[index - 1]! - 0.001)).toBe(true);
  });

  it("closes the area down to the baseline and needs two points to fill", () => {
    const area = smoothArea(points([10, 20]), 37);
    expect(area.startsWith("M 0 10")).toBe(true);
    expect(area.endsWith("L 10 37 L 0 37 Z")).toBe(true);
    expect(smoothArea(points([10]), 37)).toBe("");
    expect(smoothArea([], 37)).toBe("");
  });

  it("handles a single point without drawing a segment", () => {
    expect(smoothLine(points([12]))).toBe("M 0 12");
    expect(smoothLine([])).toBe("");
  });
});
