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
  targetLabel: string;
  canAdd: boolean;
}
export interface CollectorState {
  key: string;
  target: string;
  displayName: string;
  state: string;
  error: string | null;
  configuration: Record<string, string | number> | null;
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
export interface CollectorOperation {
  action: "configure" | "start" | "pause" | "remove";
  key: string;
  target: string;
  configuration?: Record<string, string | number>;
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
export async function operateCollector(token: string, hub: string, operation: CollectorOperation) {
  const result = await request(`/${encodeURIComponent(hub)}/operations`, token, operation);
  if (!result.succeeded) throw new Error(result.error ?? "操作未完成，请刷新状态后重试。");
}
export async function retireHub(token: string, hub: string) {
  await request(`/${encodeURIComponent(hub)}/retire`, token, {});
}

export interface DeliveryActivity {
  epoch: string;
  capturedAt: number;
  buckets: { second: number; received: number; sent: number; confirmed: number }[];
}

export async function fetchHubActivity(
  token: string,
  signal?: AbortSignal,
): Promise<{ activities: Record<string, DeliveryActivity> }> {
  return request("/activity", token, undefined, signal);
}
