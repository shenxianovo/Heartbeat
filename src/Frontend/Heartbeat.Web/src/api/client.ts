import type { EntityEnvelope, EntityResponse, ProblemDetails } from "./types";
import { isUuidv7 } from "@/lib/uuid";

const BASE = "/api";

export class ApiError extends Error {
  readonly status: number;
  readonly detail: string;
  constructor(status: number, title: string, detail: string) {
    super(detail || title || `HTTP ${status}`);
    this.name = "ApiError";
    this.status = status;
    this.detail = detail;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      headers: init?.body ? { "Content-Type": "application/json", ...init.headers } : init?.headers,
      ...init,
    });
  } catch (cause) {
    throw new ApiError(0, "Network Error", "无法连接 Heartbeat API，请确认后端（:8080）正在运行。", {
      cause,
    } as ErrorOptions);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (!res.ok) {
    let problem: ProblemDetails | undefined;
    try {
      problem = (JSON.parse(text) ?? undefined) as ProblemDetails | undefined;
    } catch {
      problem = undefined;
    }
    throw new ApiError(res.status, problem?.title ?? res.statusText, problem?.detail ?? text);
  }
  return JSON.parse(text) as T;
}

export const api = {
  getEntity(id: string): Promise<EntityResponse> {
    if (!isUuidv7(id)) return Promise.reject(new ApiError(400, "Bad Request", "不是合法的 UUIDv7 标识。"));
    return request<EntityResponse>(`/entities/${id}`);
  },

  saveObserver(id: string, name: string): Promise<void> {
    return saveEntity("observers", id, { references: {}, properties: { name } });
  },

  saveSchema(id: string, envelope: EntityEnvelope): Promise<void> {
    return saveEntity("schemas", id, envelope);
  },

  saveObservation(id: string, envelope: EntityEnvelope): Promise<void> {
    return saveEntity("observations", id, envelope);
  },

  saveBusiness(resourceName: string, id: string, envelope: EntityEnvelope): Promise<void> {
    return saveEntity(resourceName, id, envelope);
  },
};

function saveEntity(segment: string, id: string, envelope: EntityEnvelope): Promise<void> {
  if (!isUuidv7(id)) return Promise.reject(new ApiError(400, "Bad Request", "不是合法的 UUIDv7 标识。"));
  return request<void>(`/entities/${segment}/${id}`, {
    method: "PUT",
    body: JSON.stringify(envelope),
  });
}
