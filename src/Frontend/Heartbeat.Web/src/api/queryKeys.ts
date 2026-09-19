export const queryKeys = {
  tracks: (ownerSubject: string) => ["owner", ownerSubject, "tracks"] as const,
  records: (ownerSubject: string, trackId: string, from: string, to: string) =>
    ["owner", ownerSubject, "records", trackId, from, to] as const,
  replayTrack: (
    ownerSubject: string,
    trackId: string,
    from: string,
    to: string,
    bucketSeconds: number,
  ) => ["owner", ownerSubject, "replay-track", trackId, from, to, bucketSeconds] as const,
  densityWindow: (ownerSubject: string, from: string, to: string) =>
    ["owner", ownerSubject, "density-tiles", from, to] as const,
};
