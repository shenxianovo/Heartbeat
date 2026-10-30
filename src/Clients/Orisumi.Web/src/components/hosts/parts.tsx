import { Laptop, Server } from "lucide-react";
import { runLabels, type Host, type RunState } from "@/api/management";
import { ago, parseTime } from "@/lib/time";
import { cn } from "@/lib/utils";

export function HostIcon({ host, size = "md" }: { host: Host; size?: "md" | "lg" }) {
  const Icon = host.kind === "server" ? Server : Laptop;
  return (
    <span
      aria-hidden="true"
      className={cn(
        "bg-panel text-fg-2 grid shrink-0 place-items-center",
        size === "lg" ? "size-10 rounded-[10px]" : "size-9 rounded-[10px]",
        host.revoked && "opacity-50",
      )}
    >
      <Icon className="size-[18px]" />
    </span>
  );
}

/** 托管方现在能不能联系上：在线、离线多久，或者已移除。 */
export function connectionText(host: Host, now: number): string {
  if (host.revoked) return "已移除";
  if (host.connected) return "在线";
  const seen = parseTime(host.lastSeenAt);
  return seen ? `离线 · ${ago(now - seen)}联系过` : "还没连接过";
}

export function ConnectionDot({ host }: { host: Host }) {
  return <StateDot state={host.revoked ? "unknown" : host.connected ? "running" : "stopped"} />;
}

/** 运行状态的点：运行是实心，过渡中闪动，停止是空心，失败是红色，未知是虚线。 */
export function StateDot({ state }: { state: RunState | "unknown" }) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        "inline-block size-[7px] shrink-0 rounded-full",
        state === "running" && "bg-fg-2",
        (state === "starting" || state === "stopping" || state === "waiting") && "bg-fg-3 beat",
        state === "stopped" && "border-fg-3 border-[1.5px]",
        state === "failed" && "bg-danger",
        state === "unknown" && "border-fg-3 border border-dashed",
      )}
    />
  );
}

export function RunLabel({ state }: { state: RunState | "unknown" }) {
  return (
    <span className={cn("inline-flex items-center gap-1.5", state === "failed" ? "text-danger" : "text-fg-2")}>
      <StateDot state={state} />
      {runLabels[state]}
    </span>
  );
}
