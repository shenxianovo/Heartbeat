import type { TimelineRecord, ObservedObject } from "@/api/types";
import {
  clipExplicitRange,
  coveredMilliseconds,
  type ObservationInterval,
} from "@/lib/recording/intervals";
import { objectKind, objectName } from "@/components/objects/model";

export interface OverviewSource {
  id: string;
  name: string;
  label: string;
  kind: "vrchat" | "desktop" | "other";
}
export function overviewSources(objects: ObservedObject[]): OverviewSource[] {
  return objects
    .filter(
      (object) =>
        object.roles.some((role) => role === "device" || role === "account") ||
        !(object.namespace.startsWith("app.") || object.namespace.startsWith("vrchat.")),
    )
    .map((object) => ({
      id: object.id,
      name: objectName(object),
      label: objectKind(object.namespace),
      kind:
        object.namespace === "device"
          ? "desktop"
          : object.namespace === "vrchat.account"
            ? "vrchat"
            : "other",
    }));
}
export function summarizeSource(
  source: OverviewSource,
  records: TimelineRecord[],
  from: number,
  to: number,
) {
  const intervals: ObservationInterval[] = [];
  const subjects = new Set<string>();
  for (const record of records) {
    const interval = clipExplicitRange(record, from, to);
    if (!interval) continue;
    intervals.push(interval);
    for (const object of record.objects)
      if (object.role === (source.kind === "vrchat" ? "world" : "application"))
        subjects.add(object.id);
  }
  return {
    milliseconds: coveredMilliseconds(intervals),
    subjects: subjects.size,
    invalid: 0,
    days: dailyDurations(intervals, from, to),
  };
}

/** Calendar boundaries are local midnights, including short and long DST days. */
function dailyDurations(intervals: ObservationInterval[], from: number, to: number) {
  const days: { from: number; to: number; milliseconds: number }[] = [];
  for (let start = from; start < to;) {
    const midnight = new Date(start);
    midnight.setHours(0, 0, 0, 0);
    midnight.setDate(midnight.getDate() + 1);
    const end = Math.min(midnight.getTime(), to);
    const clipped = intervals
      .filter((interval) => interval.from < end && interval.to > start)
      .map((interval) => ({
        from: Math.max(start, interval.from),
        to: Math.min(end, interval.to),
      }));
    days.push({ from: start, to: end, milliseconds: coveredMilliseconds(clipped) });
    start = end;
  }
  return days;
}
