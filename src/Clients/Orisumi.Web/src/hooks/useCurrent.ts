import { useMemo } from "react";
import { ACTIVE_WINDOW } from "@/lib/activity";
import { groupThings, summarize } from "@/lib/day";
import { dayRange } from "@/lib/time";
import { productGroup, useCatalog } from "./useApplications";
import { useDay } from "./useDay";

/**
 * 今天现在的状态：当前时段的事物和它从什么时候开始，以及有几个 Observer 正在写入。
 * 侧栏在每一页显示它；数据与日页面共用同一份查询。
 */
export function useCurrent(now: number) {
  const day = useDay(dayRange(now).key);
  const catalog = useCatalog();
  const things = useMemo(() => {
    if (!day.data || !catalog.data) return [];
    const experiences = groupThings(day.data.experiences, productGroup(catalog.data));
    return summarize(experiences, [], day.range.start, day.range.end).things;
  }, [day.data, catalog.data, day.range.start, day.range.end]);

  let current: { row: (typeof things)[number]; since: number } | null = null;
  for (const row of things) {
    const segment = row.segments.find((s) => s.ongoing && now - s.end < ACTIVE_WINDOW);
    if (segment) {
      current = { row, since: segment.start };
      break;
    }
  }
  const writing = new Set(
    (day.data?.experiences ?? []).filter((e) => now - e.writtenAt < ACTIVE_WINDOW).map((e) => e.observerEntityId),
  ).size;
  return { current, writing, loading: !day.data || !catalog.data };
}
