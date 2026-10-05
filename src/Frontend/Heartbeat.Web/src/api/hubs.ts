import { ApiError } from "@/api/client";

export interface CollectorField {
  name: string;
  label: string;
  kind: string;
  required: boolean;
}
export interface CollectorType {
  key: string;
  displayName: string;
  fields: CollectorField[];
}
export interface CollectorState {
  key: string;
  target: string;
  displayName: string;
  state: string;
  error: string | null;
}
export interface HubSummary {
  id: string;
  lastSeenAt: string;
  online: boolean;
  retired: boolean;
  report: {
    displayName: string;
    kind: string;
    types: CollectorType[];
    collectors: CollectorState[];
    delivery: { pending: number; failed: number; error: string | null };
  };
}
export interface CollectorLoginRequest {
  key: string;
  input: Record<string, string>;
  target?: string;
  sessionId?: string;
}
export interface CollectorLoginResult {
  target: string | null;
  fields: CollectorField[];
  error: string | null;
  sessionId: string | null;
}

async function request(path: string, token: string, body?: unknown, signal?: AbortSignal) {
  const response = await fetch(`/api/v1/hubs${path}`, {
    method: body === undefined ? "GET" : "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new ApiError(problem.title ?? `请求失败（${response.status}）`, response.status);
  }
  return response.status === 204 ? null : response.json();
}
export async function fetchHubs(
  token: string,
  signal?: AbortSignal,
): Promise<{ hubs: HubSummary[] }> {
  return request("", token, undefined, signal);
}
export async function loginCollector(
  token: string,
  hub: string,
  login: CollectorLoginRequest,
): Promise<CollectorLoginResult> {
  const result = await request(`/${encodeURIComponent(hub)}/login`, token, login);
  if (!result.succeeded || !result.login)
    throw new Error(result.error ?? "登录未完成，请刷新状态后重试。");
  return result.login;
}

export interface DeliveryActivity {
  epoch: string;
  capturedAt: number;
  accepted: number;
  delivered: number;
}

export async function fetchHubActivity(
  token: string,
  signal?: AbortSignal,
): Promise<{ activities: Record<string, DeliveryActivity> }> {
  return request("/activity", token, undefined, signal);
}
