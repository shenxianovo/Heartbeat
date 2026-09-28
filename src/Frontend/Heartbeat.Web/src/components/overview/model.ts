import type { TimelineRecord, TrackSummary } from "@/api/types";
import { summarizeDesktopApplication } from "@/components/records/renderers/DesktopApplicationForegroundV1";
import { readLocation } from "@/components/vrchat/model";

export interface OverviewSource {
  id: string;
  name: string;
  label: string;
  kind: "vrchat" | "desktop" | "other";
  track?: TrackSummary;
}

function source(tracks: TrackSummary[]): OverviewSource {
  const first = tracks[0]!;
  const supported = tracks.filter((track) => track.version === 1 && track.endMode === "explicit");
  const location = supported.find((track) => track.type === "vrchat.location");
  const application = supported.find((track) => track.type === "desktop.application.foreground");
  if (location)
    return {
      id: first.collectorId,
      name: first.collectorDisplayName,
      label: "VRChat",
      kind: "vrchat",
      track: location,
    };
  return {
    id: first.collectorId,
    name: first.collectorDisplayName,
    label: first.collectorDisplayName,
    kind: application ? "desktop" : "other",
    track: application,
  };
}

export function overviewSources(tracks: TrackSummary[]): OverviewSource[] {
  const grouped = new Map<string, TrackSummary[]>();
  for (const track of tracks) {
    const group = grouped.get(track.collectorId) ?? [];
    group.push(track);
    grouped.set(track.collectorId, group);
  }
  return [...grouped.values()].map(source);
}

export function summarizeSource(
  source: OverviewSource,
  records: TimelineRecord[],
  from: number,
  to: number,
) {
  const intervals: { start: number; end: number }[] = [];
  const subjects = new Set<string>();
  let invalid = 0;
  for (const record of records) {
    try {
      const subject =
        source.kind === "vrchat"
          ? readLocation(record.value).world_id
          : summarizeDesktopApplication(record.value).group!.id;
      const start = Math.max(from, Date.parse(record.startedAt));
      const end = Math.min(to, Date.parse(record.endedAt ?? ""));
      if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) continue;
      intervals.push({ start, end });
      subjects.add(subject);
    } catch {
      invalid++;
    }
  }
  // Only union overlapping observations within this Collector. Sources remain separate.
  let end = -Infinity;
  let milliseconds = 0;
  for (const interval of intervals.sort((a, b) => a.start - b.start)) {
    milliseconds += Math.max(0, interval.end - Math.max(interval.start, end));
    end = Math.max(end, interval.end);
  }
  return { milliseconds, subjects: subjects.size, invalid };
}
