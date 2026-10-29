import type {
  ListEntitiesQuery,
  ListObservationsQuery,
  ListReferrersQuery,
  ProblemDetails,
  ReferrerPage,
  Version,
  VersionList,
  VersionPage,
} from "./types";

/** 控制台读取数据的接口，HTTP 实现读后端。 */
export interface OrisumiSource {
  getVersion(versionId: string): Promise<Version>;
  getEntity(entityId: string): Promise<Version>;
  listEntities(query: ListEntitiesQuery): Promise<VersionPage>;
  listEntityVersions(entityId: string): Promise<VersionList>;
  listReferrers(entityId: string, query: ListReferrersQuery): Promise<ReferrerPage>;
  listObservations(query: ListObservationsQuery): Promise<VersionPage>;
  deleteEntity(entityId: string): Promise<void>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | undefined;

  constructor(status: number, message: string, problem?: ProblemDetails) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
  }
}

/** 404 和 410 都表示读不到：不存在或已删除。 */
export function isUnreadable(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 404 || error.status === 410);
}

function query(params: object): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null) search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : "";
}

export function createHttpSource(base = "/api"): OrisumiSource {
  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    let response: Response;
    try {
      response = await fetch(`${base}${path}`, init);
    } catch (cause) {
      throw new ApiError(0, "连不上 Orisumi API", { detail: String(cause) });
    }
    if (response.status === 204) return undefined as T;
    const text = await response.text();
    if (!response.ok) {
      let problem: ProblemDetails | undefined;
      try {
        problem = JSON.parse(text) as ProblemDetails;
      } catch {
        problem = undefined;
      }
      throw new ApiError(response.status, problem?.detail || problem?.title || `HTTP ${response.status}`, problem);
    }
    return JSON.parse(text) as T;
  }

  return {
    getVersion: (versionId) => request(`/versions/${encodeURIComponent(versionId)}`),
    getEntity: (entityId) => request(`/entities/${encodeURIComponent(entityId)}`),
    listEntities: (params) => request(`/entities${query(params)}`),
    listEntityVersions: (entityId) => request(`/entities/${encodeURIComponent(entityId)}/versions`),
    listReferrers: (entityId, params) => request(`/entities/${encodeURIComponent(entityId)}/referrers${query(params)}`),
    listObservations: (params) => request(`/observations${query(params)}`),
    deleteEntity: (entityId) => request(`/entities/${encodeURIComponent(entityId)}`, { method: "DELETE" }),
  };
}

/** 读完一个分页列表。 */
export async function readAllPages(load: (after: string | undefined) => Promise<VersionPage>): Promise<Version[]> {
  const versions: Version[] = [];
  let after: string | undefined;
  do {
    const page = await load(after);
    versions.push(...page.versions);
    after = page.next ?? undefined;
  } while (after);
  return versions;
}
