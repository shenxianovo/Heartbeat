export interface ObjectScope {
  objectId?: string;
  contextObjectIds?: string[];
}
export interface ObservedObject {
  id: string;
  namespace: string;
  key: string;
  name: string | null;
  roles: string[];
}
export interface RecordObject {
  id: string;
  role: string;
  namespace: string;
  key: string;
  name: string | null;
}

export type TimeMode = "point" | "range";

export interface TrackSummary {
  id: string;
  collectorId: string;
  collectorKey: string;
  collectorTarget: string;
  collectorDisplayName: string;
  type: string;
  version: number;
  timeMode: TimeMode;
  createdAt: string;
}

export interface TrackReference {
  id: string;
  collectorId: string;
  type: string;
  version: number;
  timeMode: TimeMode;
}

export interface TimelineRecord {
  id: string;
  startedAt: string;
  endedAt: string | null;
  observedAt: string | null;
  receivedAt: string;
  value: unknown;
  objects: RecordObject[];
}

export interface TracksResponse {
  tracks: TrackSummary[];
}

export interface RecordsResponse {
  track: TrackReference;
  records: TimelineRecord[];
  nextCursor: string | null;
}

export interface RecordsQuery extends ObjectScope {
  trackId: string;
  from: string;
  to: string;
  cursor?: string | null;
  limit?: number;
}

export interface PointCountBucket {
  index: number;
  startedAt: string;
  endedAt: string;
  count: number;
}

export interface PointCountsResponse {
  track: TrackReference;
  from: string;
  to: string;
  bucketSeconds: number;
  buckets: PointCountBucket[];
}

export interface ReplayLane {
  track: TrackSummary;
  records: TimelineRecord[];
  counts: PointCountsResponse | null;
  detailCounts?: PointCountsResponse[];
}
