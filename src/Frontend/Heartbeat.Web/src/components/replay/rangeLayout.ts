import type { TimelineRecord } from "@/api/types";
import { overlaps } from "./timeRange";

export interface RangeItem {
  record: TimelineRecord;
  row: number;
  start: number;
  end: number;
}

/**
 * Bar geometry in CSS pixels, shared by the plot, hit testing and the lane height.
 * `minWidth` keeps a one-second observation visible at any zoom.
 */
export const rowGeometry = { top: 8, pitch: 28, bar: 20, bottom: 12, minWidth: 2 };

export function rowTop(row: number): number {
  return rowGeometry.top + row * rowGeometry.pitch;
}

export function laneHeight(rows: number): number {
  return rows * rowGeometry.pitch + rowGeometry.bottom;
}

// API pages and their filtered sublanes are already sorted by (startedAt, id).
// Place overlapping observations on distinct rows without sorting each frame.
export function layoutRanges(records: TimelineRecord[], from: number, to: number) {
  const ends: number[] = [];
  const items: RangeItem[] = [];
  for (const record of records) {
    const rawStart = Date.parse(record.startedAt);
    const rawEnd = Date.parse(record.endedAt ?? record.startedAt);
    if (!Number.isFinite(rawStart) || !Number.isFinite(rawEnd)) continue;
    if (!overlaps(rawStart, rawEnd, { start: from, end: to })) continue;
    const start = Math.max(from, rawStart);
    const end = Math.min(to, rawEnd);
    if (end < start) continue;
    let row = ends.findIndex((previousEnd) => previousEnd <= start);
    if (row < 0) row = ends.length;
    ends[row] = end;
    items.push({ record, row, start, end });
  }
  return { items, rows: Math.max(1, ends.length) };
}
