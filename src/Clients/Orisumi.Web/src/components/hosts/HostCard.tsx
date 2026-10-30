import { useState } from "react";
import { MoreHorizontal, Plus } from "lucide-react";
import { problem, runState, synced, type Host, type Instance } from "@/api/management";
import { Anchored, Button, Chip, MenuItem, Switch } from "@/components/ui";
import { Mark } from "@/components/token/Token";
import { useManage } from "@/hooks/useManagement";
import { cn } from "@/lib/utils";
import { ConnectionDot, connectionText, HostIcon, RunLabel } from "./parts";

/** 一个托管方和它的程序实例。实例按启用开关、运行状态和待发送一行一个，点名字打开面板。 */
export function HostCard({
  host,
  instances,
  now,
  selected,
  onSelect,
  onInstall,
  onPair,
}: {
  host: Host;
  instances: Instance[];
  now: number;
  selected: string | undefined;
  onSelect: (entityId: string) => void;
  onInstall: () => void;
  onPair: () => void;
}) {
  const manage = useManage();
  const [renaming, setRenaming] = useState(false);
  const [menu, setMenu] = useState<"closed" | "main" | "revoke">("closed");
  const current = instances.filter((instance) => !instance.removed);
  const removed = instances.filter((instance) => instance.removed);

  return (
    <article aria-label={host.name} className="bg-card border-line shadow-card rounded-2xl border">
      <header className="flex items-center gap-3 px-4 pt-3.5 pb-3">
        <HostIcon host={host} />
        <div className="min-w-0 flex-1">
          {renaming ? (
            <form
              onSubmit={(event) => {
                event.preventDefault();
                const name = String(new FormData(event.currentTarget).get("name") ?? "").trim();
                setRenaming(false);
                if (name && name !== host.name)
                  manage.mutate({ path: `/hosts/${host.hostId}`, method: "PATCH", body: { name } });
              }}
            >
              <input
                name="name"
                autoFocus
                defaultValue={host.name}
                aria-label="名称"
                onBlur={(event) => event.currentTarget.form?.requestSubmit()}
                onKeyDown={(event) => {
                  if (event.key !== "Escape") return;
                  event.preventDefault();
                  setRenaming(false);
                }}
                className="border-line-3 w-full rounded-md border bg-transparent px-1.5 py-0.5 text-[15px] font-medium outline-none"
              />
            </form>
          ) : (
            <h2 className={cn("truncate text-[15px] font-medium", host.revoked && "text-fg-3")}>{host.name}</h2>
          )}
          <p className="text-fg-3 flex items-center gap-1.5 text-xs">
            <ConnectionDot host={host} />
            {host.kind === "server" ? "服务器" : "设备"} · {connectionText(host, now)}
          </p>
        </div>
        {!host.revoked && (
          <Button
            onClick={onInstall}
            disabled={!host.connected}
            title={host.connected ? undefined : "设备连接后才能安装"}
          >
            <Plus className="size-3.5" />
            安装
          </Button>
        )}
        <Anchored
          align="end"
          open={menu !== "closed"}
          onClose={() => setMenu("closed")}
          trigger={
            <button
              type="button"
              aria-label={`${host.name} 的更多操作`}
              onClick={() => setMenu(menu === "closed" ? "main" : "closed")}
              className="text-fg-3 hover:text-fg hover:bg-panel cursor-pointer rounded-md p-1"
            >
              <MoreHorizontal className="size-4" />
            </button>
          }
        >
          {menu === "revoke" ? (
            <div className="space-y-2 p-1.5">
              <p className="text-[13px]">移除 {host.name}？</p>
              <p className="text-fg-3 text-xs leading-relaxed">
                它不能再同步和发送，已保存的数据都留着。之后可以重新接入，沿用原来的身份。
              </p>
              <div className="flex justify-end gap-2 pt-1">
                <Button onClick={() => setMenu("main")}>取消</Button>
                <Button
                  danger
                  onClick={() => {
                    setMenu("closed");
                    manage.mutate({ path: `/hosts/${host.hostId}/revoke`, method: "POST" });
                  }}
                >
                  移除
                </Button>
              </div>
            </div>
          ) : (
            <>
              <MenuItem
                onClick={() => {
                  setMenu("closed");
                  setRenaming(true);
                }}
              >
                改名
              </MenuItem>
              {host.revoked ? (
                <MenuItem
                  onClick={() => {
                    setMenu("closed");
                    onPair();
                  }}
                >
                  重新接入…
                </MenuItem>
              ) : (
                <MenuItem danger onClick={() => setMenu("revoke")}>
                  移除…
                </MenuItem>
              )}
            </>
          )}
        </Anchored>
      </header>
      {manage.error && <p className="text-danger px-4 pb-2 text-xs">{manage.error.message}</p>}
      {current.length ? (
        <ul aria-label={`${host.name} 的 Observer`} className="px-2 pb-2">
          {current.map((instance) => (
            <InstanceRow
              key={instance.entityId}
              host={host}
              instance={instance}
              selected={selected === instance.entityId}
              onSelect={() => onSelect(instance.entityId)}
            />
          ))}
        </ul>
      ) : (
        <p className="text-fg-3 border-line border-t px-4 py-3 text-[13px]">
          {host.revoked ? "没有 Observer" : "还没有 Observer"}
        </p>
      )}
      {removed.length > 0 && (
        <details className="border-line text-fg-3 border-t px-4 py-2 text-xs">
          <summary className="hover:text-fg-2 cursor-pointer">已删除 {removed.length} 个实例</summary>
          <ul className="mt-1 space-y-1 pb-1">
            {removed.map((instance) => (
              <li key={instance.entityId} className="flex justify-between gap-3">
                <span>{instance.name}</span>
                <span>{instance.status?.state === "stopped" ? "已停止" : "等待设备停止"}</span>
              </li>
            ))}
          </ul>
        </details>
      )}
    </article>
  );
}

function InstanceRow({
  host,
  instance,
  selected,
  onSelect,
}: {
  host: Host;
  instance: Instance;
  selected: boolean;
  onSelect: () => void;
}) {
  const manage = useManage();
  const issue = problem(instance);
  const pending = instance.status?.delivery?.pending ?? 0;
  return (
    <li
      className={cn(
        "border-line grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 rounded-lg border-t px-2 py-2.5 first:border-t-0",
        selected && "bg-panel",
      )}
    >
      <button type="button" onClick={onSelect} className="flex min-w-0 cursor-pointer items-center gap-2.5 text-left">
        <Mark role="source" color={null} size="sm" />
        <span className="min-w-0">
          <span className="flex items-baseline gap-2">
            <span className="truncate text-[13px]">{instance.name}</span>
            {instance.status?.version && (
              <span className="text-fg-3 font-mono text-[11px]">{instance.status.version}</span>
            )}
          </span>
          <span className="text-fg-3 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs">
            <RunLabel state={runState(host, instance)} />
            {pending > 0 && <span className="tabular-nums">{pending} 待发送</span>}
            {issue && <span className="text-danger truncate">{issue}</span>}
          </span>
        </span>
      </button>
      <span className="flex items-center gap-3">
        {!synced(instance) && !host.revoked && <Chip dashed>等待同步</Chip>}
        <Switch
          label={`启用 ${instance.name}`}
          checked={instance.enabled}
          disabled={host.revoked || manage.isPending}
          onChange={(enabled) =>
            manage.mutate({ path: `/observer-instances/${instance.entityId}`, method: "PATCH", body: { enabled } })
          }
        />
      </span>
    </li>
  );
}
