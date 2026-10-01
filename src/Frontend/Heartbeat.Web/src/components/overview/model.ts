import type { TimelineRecord, TrackSummary } from "@/api/types";
import { summarizeDesktopApplication } from "@/components/records/renderers/DesktopApplicationForegroundV1";
import { readLocation } from "@/components/vrchat/model";
import {
  clipExplicitRange,
  coveredMilliseconds,
  type ObservationInterval,
} from "@/lib/recording/intervals";

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
  const intervals: ObservationInterval[] = [];
  const subjects = new Set<string>();
  let invalid = 0;
  for (const record of records) {
    try {
      const subject =
        source.kind === "vrchat"
          ? readLocation(record.value).world_id
          : summarizeDesktopApplication(record.value).group!.id;
      const interval = clipExplicitRange(record, from, to);
      if (!interval) continue;
      intervals.push(interval);
      subjects.add(subject);
    } catch {
      invalid++;
    }
  }
  // Only union overlapping observations within this Collector. Sources remain separate.
  return { milliseconds: coveredMilliseconds(intervals), subjects: subjects.size, invalid };
}
