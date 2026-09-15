import type {
  PointCountsResponse,
  RecordsQuery,
  RecordsResponse,
  TracksResponse,
} from "@/api/types";

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

async function readError(response: Response): Promise<string> {
  try {
    const body: unknown = await response.json();
    if (typeof body === "object" && body !== null) {
      const detail = "detail" in body ? body.detail : null;
      const title = "title" in body ? body.title : null;
      const code = "code" in body ? body.code : null;
      if (typeof detail === "string") return detail;
      if (typeof title === "string") return title;
      if (typeof code === "string") return code;
    }
  } catch {
    // Some error responses intentionally have no JSON body.
  }
  return `请求失败（${response.status}）`;
}

async function getJson<T>(url: string, accessToken: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    headers: {
      Accept: "application/json",
      Authorization: `Bearer ${accessToken}`,
    },
    signal,
  });

  if (!response.ok) {
    throw new ApiError(await readError(response), response.status);
  }

  return (await response.json()) as T;
}

export function fetchTracks(accessToken: string, signal?: AbortSignal): Promise<TracksResponse> {
  return getJson<TracksResponse>("/api/v1/tracks", accessToken, signal);
}

export function fetchRecords(
  accessToken: string,
  query: RecordsQuery,
  signal?: AbortSignal,
): Promise<RecordsResponse> {
  const params = new URLSearchParams({
    from: query.from,
    to: query.to,
    limit: String(query.limit ?? 100),
  });
  if (query.cursor) params.set("cursor", query.cursor);

  return getJson<RecordsResponse>(
    `/api/v1/tracks/${encodeURIComponent(query.trackId)}/records?${params}`,
    accessToken,
    signal,
  );
}

export async function fetchAllRecords(
  accessToken: string,
  query: Omit<RecordsQuery, "cursor" | "limit">,
  signal?: AbortSignal,
): Promise<RecordsResponse> {
  const records: RecordsResponse["records"] = [];
  let cursor: string | null = null;
  let track: RecordsResponse["track"] | null = null;
  do {
    const page = await fetchRecords(accessToken, { ...query, cursor, limit: 500 }, signal);
    track = page.track;
    records.push(...page.records);
    cursor = page.nextCursor;
  } while (cursor);
  if (!track) throw new Error("Record replay returned no Track metadata.");
  return { track, records, nextCursor: null };
}

export function fetchPointCounts(
  accessToken: string,
  trackId: string,
  from: string,
  to: string,
  bucketSeconds: number,
  signal?: AbortSignal,
): Promise<PointCountsResponse> {
  const params = new URLSearchParams({ from, to, bucketSeconds: String(bucketSeconds) });
  return getJson<PointCountsResponse>(
    `/api/v1/tracks/${encodeURIComponent(trackId)}/point-counts?${params}`,
    accessToken,
    signal,
  );
}
