import { ApiError } from "./client";

export interface Host {
  hostId: string;
  name: string;
  kind: "device" | "server";
  revoked: boolean;
  connected: boolean;
  lastSeenAt: string | null;
}

export type RunState = "starting" | "running" | "stopping" | "waiting" | "stopped" | "failed";

export interface InstanceStatus {
  packageId?: string;
  version?: string | null;
  installed?: boolean;
  enabled?: boolean;
  state?: RunState;
  error?: string | null;
  diagnostic?: string | null;
  appliedRevision?: number;
  pendingChanges?: number;
  pendingWrites?: number;
  rejectedWrites?: number;
  lastRejection?: string | null;
}

export interface Instance {
  entityId: string;
  hostId: string;
  packageId: string;
  name: string;
  enabled: boolean;
  settings: Record<string, unknown>;
  revision: number;
  removed: boolean;
  reportedAt: string | null;
  status: InstanceStatus | null;
}

export type Action = "catalog" | "install" | "update" | "uninstall" | "retry" | "resendRejected";

export interface Operation {
  operationId: string;
  hostId: string;
  action: Action;
  entityId: string | null;
  packageId: string | null;
  state: "queued" | "delivered" | "succeeded" | "failed" | "expired" | "unconfirmed";
  error: string | null;
  result: { packages?: Package[]; entityId?: string } | null;
}

export interface Package {
  id: string;
  name: string;
  description?: string | null;
  version: string;
  compatible: boolean;
  installedVersion: string | null;
}

export interface Pairing {
  code: string;
  expiresAt: string;
  hostId: string | null;
}

export async function management<T>(path: string, method = "GET", body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    ...(body === undefined ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }),
  });
  if (response.status === 204) return undefined as T;
  const value: unknown = await response.json();
  if (!response.ok) {
    const problem = value as { detail?: string; title?: string };
    throw new ApiError(response.status, problem.detail ?? problem.title ?? `HTTP ${response.status}`);
  }
  return value as T;
}

/** 托管方没有连接时，最近的报告只说明当时的情况，所以运行状态是未知。 */
export function runState(host: Host, instance: Instance): RunState | "unknown" {
  if (!host.connected || !instance.status?.state) return "unknown";
  return instance.status.state;
}

export const runLabels: Record<RunState | "unknown", string> = {
  running: "运行中",
  starting: "启动中",
  stopping: "停止中",
  waiting: "等待重启",
  stopped: "已停止",
  failed: "运行失败",
  unknown: "状态未知",
};

/** 后端接受的配置是否已经被设备执行。 */
export function synced(instance: Instance): boolean {
  return instance.status?.appliedRevision === instance.revision && !instance.status.pendingChanges;
}

export function problem(instance: Instance): string | null {
  const status = instance.status;
  return status?.error ?? null;
}

export function settled(operation: Operation | undefined): boolean {
  return !operation || !["queued", "delivered"].includes(operation.state);
}
