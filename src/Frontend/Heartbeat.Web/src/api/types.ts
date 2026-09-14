export type TimeMode = "point" | "range";
export type EndMode = "explicit" | "next_record" | null;

export interface TrackSummary {
  id: string;
  collectorId: string;
  collectorKey: string;
  collectorTarget: string;
  collectorDisplayName: string;
  type: string;
  version: number;
  timeMode: TimeMode;
  endMode: EndMode;
  createdAt: string;
}

export interface TrackReference {
  id: string;
  collectorId: string;
  type: string;
  version: number;
  timeMode: TimeMode;
  endMode: EndMode;
}

export interface TimelineRecord {
  id: string;
  startedAt: string;
  endedAt: string | null;
  observedAt: string | null;
  receivedAt: string;
  value: unknown;
}

export interface TracksResponse {
  tracks: TrackSummary[];
}

export interface RecordsResponse {
  track: TrackReference;
  records: TimelineRecord[];
  nextCursor: string | null;
}

export interface RecordsQuery {
  trackId: string;
  from: string;
  to: string;
  cursor?: string | null;
  limit?: number;
}
