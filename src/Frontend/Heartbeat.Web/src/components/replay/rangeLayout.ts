import type { TimelineRecord } from "@/api/types";

// Track permits overlapping observations. Place them on distinct rows without
// changing their time bounds or interpreting their protocol values.
export function layoutRanges(records: TimelineRecord[], from: number, to: number) {
  const ends: number[] = [];
  const items = [...records]
    .sort((a, b) => Date.parse(a.startedAt) - Date.parse(b.startedAt) || a.id.localeCompare(b.id))
    .flatMap((record) => {
      const start = Math.max(from, Date.parse(record.startedAt));
      const end = Math.min(to, Date.parse(record.endedAt ?? record.startedAt));
      if (
        !Number.isFinite(start) ||
        !Number.isFinite(end) ||
        start >= to ||
        end < from ||
        end < start
      )
        return [];
      let row = ends.findIndex((previousEnd) => previousEnd <= start);
      if (row < 0) row = ends.length;
      ends[row] = end;
      return [{ record, row, start, end }];
    });
  return { items, rows: Math.max(1, ends.length) };
}
