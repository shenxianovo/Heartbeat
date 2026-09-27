"use client";

import type { CollectorState, CollectorType } from "@/api/hubs";
import { Button } from "@/components/ui/button";

const states: Record<string, { label: string; tone: string }> = {
  running: { label: "采集中", tone: "success" },
  paused: { label: "已暂停", tone: "muted" },
  error: { label: "发生错误", tone: "danger" },
  authentication_required: { label: "需要登录", tone: "danger" },
};

export function CollectorRow({
  collector,
  type,
  disabled,
  stale,
  login,
}: {
  collector: CollectorState;
  type?: CollectorType;
  disabled: boolean;
  stale: boolean;
  login: (form: { type: CollectorType; target: string }) => void;
}) {
  return (
    <div className="collector-row">
      <div>
        <strong>{collector.displayName}</strong>
        <span
          className="status-indicator mt-1.5"
          data-tone={stale ? "muted" : states[collector.state]?.tone}
        >
          {stale && "最近上报："}
          {states[collector.state]?.label ?? collector.state}
        </span>
        <details className="hub-identifiers">
          <summary>账号标识</summary>
          <p>{collector.target}</p>
        </details>
        {collector.error && <p role="status">{collector.error}</p>}
      </div>
      {type && collector.state === "authentication_required" && (
        <Button
          size="sm"
          className="max-w-full whitespace-normal"
          disabled={disabled}
          onClick={() => login({ type, target: collector.target })}
        >
          重新登录
        </Button>
      )}
    </div>
  );
}
