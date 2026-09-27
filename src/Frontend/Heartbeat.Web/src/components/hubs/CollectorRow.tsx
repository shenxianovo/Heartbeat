"use client";

import type { CollectorState, CollectorType } from "@/api/hubs";
import { Button } from "@/components/ui/button";

const stateNames: Record<string, string> = {
  running: "采集中",
  paused: "已暂停",
  error: "发生错误",
  authentication_required: "需要登录",
};

export function CollectorRow({
  collector,
  type,
  disabled,
  login,
}: {
  collector: CollectorState;
  type?: CollectorType;
  disabled: boolean;
  login: (form: { type: CollectorType; target: string }) => void;
}) {
  return (
    <div className="collector-row">
      <div>
        <strong>{collector.displayName}</strong>
        <p>{collector.target}</p>
        <span>{stateNames[collector.state] ?? collector.state}</span>
        {collector.error && <p role="status">{collector.error}</p>}
      </div>
      {type && collector.state === "authentication_required" && (
        <Button disabled={disabled} onClick={() => login({ type, target: collector.target })}>
          重新登录
        </Button>
      )}
    </div>
  );
}
