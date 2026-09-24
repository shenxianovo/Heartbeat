"use client";

import type { CollectorOperation, CollectorState, CollectorType } from "@/api/hubs";
import { Button } from "@/components/ui/Button";

const stateNames: Record<string, string> = {
  running: "采集中",
  paused: "已暂停",
  error: "发生错误",
  authentication_required: "需要认证",
};

export function CollectorRow({
  collector,
  type,
  disabled,
  operate,
  edit,
}: {
  collector: CollectorState;
  type?: CollectorType;
  disabled: boolean;
  operate: (operation: CollectorOperation) => Promise<void>;
  edit: (form: { type: CollectorType; collector: CollectorState }) => void;
}) {
  const cannotStart = ["running", "authentication_required"].includes(collector.state);
  function execute(action: CollectorOperation["action"]) {
    return operate({ action, key: collector.key, target: collector.target });
  }
  function remove() {
    if (window.confirm(`移除 ${collector.displayName} 的采集配置？已保存的 Record 会保留。`))
      void execute("remove");
  }
  return (
    <div className="collector-row">
      <div>
        <strong>{collector.displayName}</strong>
        <p>{collector.target}</p>
        <span>{stateNames[collector.state] ?? collector.state}</span>
        {collector.error && <p role="status">{collector.error}</p>}
      </div>
      <div className="hub-actions">
        <Button disabled={disabled || cannotStart} onClick={() => void execute("start")}>
          开始
        </Button>
        <Button
          disabled={disabled || collector.state === "paused"}
          onClick={() => void execute("pause")}
        >
          暂停
        </Button>
        {type?.canAdd && (
          <>
            <Button disabled={disabled} onClick={() => edit({ type, collector })}>
              配置
            </Button>
            <Button disabled={disabled} onClick={remove}>
              移除
            </Button>
          </>
        )}
      </div>
    </div>
  );
}
