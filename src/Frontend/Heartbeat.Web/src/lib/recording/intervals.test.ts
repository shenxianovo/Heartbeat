import { describe, expect, it } from "vitest";
import { clipExplicitRange, coveredMilliseconds } from "./intervals";

function range(start: number, end: number | null) {
  return {
    startedAt: new Date(start).toISOString(),
    endedAt: end === null ? null : new Date(end).toISOString(),
  };
}

describe("explicit observation intervals", () => {
  it("clips to the window without changing the stored time or extending to now", () => {
    const record = Object.freeze(range(5, 25));
    expect(clipExplicitRange(record, 10, 20)).toEqual({ from: 10, to: 20 });
    expect(record).toEqual(range(5, 25));
    expect(clipExplicitRange(range(12, 16), 10, 20)).toEqual({ from: 12, to: 16 });
  });

  it("keeps zero-duration observations inside the window but excludes touching ranges", () => {
    expect(clipExplicitRange(range(0, 10), 10, 20)).toBeNull();
    expect(clipExplicitRange(range(20, 30), 10, 20)).toBeNull();
    expect(clipExplicitRange(range(10, 10), 10, 20)).toEqual({ from: 10, to: 10 });
    expect(clipExplicitRange(range(20, 20), 10, 20)).toBeNull();
  });

  it("does not invent an explicit end or accept invalid observation times and windows", () => {
    expect(clipExplicitRange(range(12, null), 10, 20)).toBeNull();
    expect(clipExplicitRange(range(16, 12), 10, 20)).toBeNull();
    expect(clipExplicitRange({ ...range(12, 16), endedAt: "invalid" }, 10, 20)).toBeNull();
    expect(clipExplicitRange(range(12, 16), 20, 10)).toBeNull();
    expect(clipExplicitRange(range(12, 16), 10, 10)).toBeNull();
    expect(clipExplicitRange(range(12, 16), NaN, 20)).toBeNull();
  });

  it("unions unordered, duplicate and nested intervals without filling gaps or sorting inputs", () => {
    const intervals = Object.freeze([
      { from: 18, to: 20 },
      { from: 10, to: 14 },
      { from: 12, to: 16 },
      { from: 11, to: 13 },
      { from: 10, to: 14 },
      { from: 17, to: 17 },
    ]);
    expect(coveredMilliseconds(intervals)).toBe(8);
    expect(intervals[0]).toEqual({ from: 18, to: 20 });
    expect(coveredMilliseconds([])).toBe(0);
  });
});
