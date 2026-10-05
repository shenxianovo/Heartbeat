import type { ObjectScope } from "./types";
const scopeKey = (scope: ObjectScope) => [
  scope.objectId ?? "",
  JSON.stringify([...new Set(scope.contextObjectIds)].sort()),
];
export const queryKeys = {
  tracks: (owner: string, scope: ObjectScope = {}) =>
    ["owner", owner, "tracks", ...scopeKey(scope)] as const,
  records: (owner: string, track: string, from: string, to: string, scope: ObjectScope = {}) =>
    ["owner", owner, "records", track, from, to, ...scopeKey(scope)] as const,
  replayTrack: (
    owner: string,
    track: string,
    from: string,
    to: string,
    bucket: number,
    scope: ObjectScope = {},
  ) => ["owner", owner, "replay-track", track, from, to, bucket, ...scopeKey(scope)] as const,
  densityWindow: (owner: string, from: string, to: string, scope: ObjectScope = {}) =>
    ["owner", owner, "density-tiles", from, to, ...scopeKey(scope)] as const,
};
