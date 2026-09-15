import { describe, expect, it } from "vitest";
import { clampRange, densityBucketSeconds, dragRange, overlaps, zoomRange } from "./timeRange";

const bounds = { start: 0, end: 86400000 };
describe("shared timeline window", () => {
  it("preserves span when panning against either day boundary", () => {
    expect(dragRange("move", { start: 2000, end: 6000 }, bounds, 2000, -10000)).toEqual({
      start: 0,
      end: 4000,
    });
    expect(clampRange({ start: 86400000, end: 86404000 }, bounds)).toEqual({
      start: 86396000,
      end: 86400000,
    });
  });
  it("keeps handles ordered and supports reverse brushing", () => {
    expect(dragRange("start", { start: 2000, end: 6000 }, bounds, 2000, 9000)).toEqual({
      start: 5000,
      end: 6000,
    });
    expect(dragRange("end", { start: 2000, end: 6000 }, bounds, 6000, 0)).toEqual({
      start: 2000,
      end: 3000,
    });
    expect(dragRange("select", bounds, bounds, 6000, 2000)).toEqual({ start: 2000, end: 6000 });
  });
  it("zooms around the pointer and cannot zoom below one second", () => {
    expect(zoomRange({ start: 10000, end: 18000 }, bounds, 0.5, 0.25)).toEqual({
      start: 11000,
      end: 15000,
    });
    expect(zoomRange({ start: 10000, end: 11000 }, bounds, 0.1)).toEqual({
      start: 10000,
      end: 11000,
    });
  });
  it("does not include an interval that ends at the viewport start", () => {
    expect(overlaps(0, 1000, { start: 1000, end: 2000 })).toBe(false);
    expect(overlaps(1000, 1000, { start: 1000, end: 2000 })).toBe(true);
  });
  it("uses finer query-time density as the viewport shrinks", () => {
    expect(densityBucketSeconds(bounds)).toBe(900);
    expect(densityBucketSeconds({ start: 0, end: 3600000 })).toBe(30);
    expect(densityBucketSeconds({ start: 0, end: 60000 })).toBe(1);
  });
});
