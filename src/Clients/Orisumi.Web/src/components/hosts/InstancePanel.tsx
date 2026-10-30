import { useEffect, useState, type ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import { X } from "lucide-react";
import { problem, runState, synced, type Action, type Host, type Instance, type Operation } from "@/api/management";
import { Button, Switch } from "@/components/ui";
import { Mark } from "@/components/token/Token";
import { useManage, useOperation } from "@/hooks/useManagement";
import { ago, parseTime } from "@/lib/time";
import { cn } from "@/lib/utils";
import { ConnectionDot, connectionText, RunLabel } from "./parts";

const ACTIONS: { action: Action; label: string; done: string }[] = [
  { action: "update", label: "更新", done: "已更新" },
  { action: "retry", label: "重试", done: "已重新启动" },
  { action: "uninstall", label: "卸载", done: "已卸载" },
];

/** 一个程序实例的面板：配置、同步、运行和连接四项分开写，下面是设置、维护和删除。 */
export function InstancePanel({
  host,
  instance,
  now,
  onClose,
}: {
  host: Host;
  instance: Instance;
  now: number;
  onClose: () => void;
}) {
  useEffect(() => {
    function key(event: KeyboardEvent) {
      if (event.key === "Escape" && !event.defaultPrevented) onClose();
    }
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, [onClose]);

  const manage = useManage();
  const reported = parseTime(instance.reportedAt);
  const writable = !instance.removed && !host.revoked;

  return (
    <aside
      aria-label={`${instance.name} 的管理`}
      className="panel-enter bg-card border-line fixed inset-y-0 right-0 z-30 flex w-[min(400px,100vw)] flex-col border-l shadow-[-16px_0_40px_-24px_rgb(0_0_0/0.35)]"
    >
      <header className="border-line flex items-start gap-3 border-b px-4 pt-4 pb-3">
        <Mark role="source" color={null} size="lg" framed />
        <div className="min-w-0 flex-1">
          <p className="text-fg-3 text-xs">程序实例 · {host.name}</p>
          <h2 className="truncate text-[17px] leading-snug font-medium">{instance.name}</h2>
        </div>
        <button
          type="button"
          aria-label="关闭"
          onClick={onClose}
          className="text-fg-3 hover:text-fg hover:bg-panel -mr-1 cursor-pointer rounded-md p-1"
        >
          <X className="size-4" />
        </button>
      </header>
      <div className="min-h-0 flex-1 space-y-5 overflow-y-auto px-4 py-4">
        <dl className="grid grid-cols-[72px_minmax(0,1fr)] gap-x-3 gap-y-2.5 text-[13px]">
          <Fact label="配置">
            <span className="flex items-center gap-2">
              <Switch
                label="启用"
                checked={instance.enabled}
                disabled={!writable || manage.isPending}
                onChange={(enabled) =>
                  manage.mutate({
                    path: `/observer-instances/${instance.entityId}`,
                    method: "PATCH",
                    body: { enabled },
                  })
                }
              />
              <span className="text-fg-2">{instance.enabled ? "启用" : "停用"}</span>
            </span>
          </Fact>
          <Fact label="同步">
            {instance.removed
              ? instance.status?.state === "stopped"
                ? "已删除，设备已停止它"
                : "已删除，等待设备停止它"
              : synced(instance)
                ? "设备已执行最新的配置"
                : "等待设备同步"}
          </Fact>
          <Fact label="运行">
            <RunLabel state={runState(host, instance)} />
            {!host.connected && reported && <span className="text-fg-3 ml-2 text-xs">{ago(now - reported)}报告</span>}
          </Fact>
          <Fact label="连接">
            <span className="inline-flex items-center gap-1.5">
              <ConnectionDot host={host} />
              {connectionText(host, now)}
            </span>
          </Fact>
          {instance.status?.version && (
            <Fact label="版本">
              <span className="font-mono text-xs">{instance.status.version}</span>
            </Fact>
          )}
          {instance.status?.delivery && (
            <Fact label="发送">
              <span className="tabular-nums">
                {instance.status.delivery.pending ? `${instance.status.delivery.pending} 个待发送` : "都已发送"}
              </span>
            </Fact>
          )}
        </dl>
        {(problem(instance) || instance.status?.diagnostic) && (
          <div className="space-y-1 text-xs leading-relaxed">
            {problem(instance) && <p className="text-danger">{problem(instance)}</p>}
            {instance.status?.diagnostic && <p className="text-fg-3">{instance.status.diagnostic}</p>}
          </div>
        )}
        {writable && <SettingsEditor instance={instance} />}
        {writable && <Maintenance host={host} instance={instance} />}
        {writable && <Removal instance={instance} onRemoved={onClose} />}
      </div>
      <footer className="border-line text-fg-3 flex items-center gap-3 border-t px-4 py-2 text-xs">
        <span className="font-mono">…{instance.entityId.slice(-8)}</span>
        <span className="flex-1" />
        <Link to="/entities/$entityId" params={{ entityId: instance.entityId }} className="hover:text-fg">
          它的记录 →
        </Link>
      </footer>
    </aside>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-fg-3">{label}</dt>
      <dd className="flex min-w-0 items-center">{children}</dd>
    </>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="border-line space-y-2 border-t pt-4">
      <h3 className="text-[13px] font-medium">{title}</h3>
      {children}
    </section>
  );
}

function SettingsEditor({ instance }: { instance: Instance }) {
  const manage = useManage();
  const saved = JSON.stringify(instance.settings, null, 2);
  const [text, setText] = useState(saved);
  const parsed = parseObject(text);
  const changed = text !== saved;
  return (
    <Section title="设置">
      <textarea
        aria-label="设置"
        spellCheck={false}
        value={text}
        onChange={(event) => setText(event.target.value)}
        className={cn(
          "bg-panel text-fg min-h-32 w-full resize-y rounded-lg border px-2.5 py-2 font-mono text-xs leading-relaxed outline-none",
          parsed ? "focus:border-line-3 border-transparent" : "border-danger",
        )}
      />
      <div className="flex items-center gap-2">
        <p className={cn("flex-1 text-xs", parsed ? "text-fg-3" : "text-danger")}>
          {parsed ? "整个对象一起保存" : "要写成一个 JSON 对象"}
        </p>
        {changed && <Button onClick={() => setText(saved)}>还原</Button>}
        <Button
          primary
          disabled={!changed || !parsed || manage.isPending}
          onClick={() =>
            manage.mutate({
              path: `/observer-instances/${instance.entityId}`,
              method: "PATCH",
              body: { settings: parsed },
            })
          }
        >
          保存
        </Button>
      </div>
      {manage.error && <p className="text-danger text-xs">{manage.error.message}</p>}
    </Section>
  );
}

function parseObject(text: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(text);
    return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function Maintenance({ host, instance }: { host: Host; instance: Instance }) {
  const { operation, error, start, running } = useOperation(host.hostId);
  return (
    <Section title="维护">
      <div className="flex gap-2">
        {ACTIONS.map(({ action, label }) => (
          <Button
            key={action}
            disabled={!host.connected || running}
            onClick={() => void start(action, { entityId: instance.entityId })}
          >
            {label}
          </Button>
        ))}
      </div>
      <p className={cn("text-xs", operation?.state === "failed" || error ? "text-danger" : "text-fg-3")}>
        {error ??
          (operation
            ? describe(operation)
            : host.connected
              ? "更新到目录中的最新版本，或重新启动失败的实例"
              : "设备连接后才能操作")}
      </p>
    </Section>
  );
}

function describe(operation: Operation): string {
  const action = ACTIONS.find((item) => item.action === operation.action);
  switch (operation.state) {
    case "queued":
    case "delivered":
      return `正在${action?.label ?? "执行"}…`;
    case "succeeded":
      return action?.done ?? "完成";
    case "failed":
      return operation.error ?? `${action?.label ?? "操作"}失败`;
    case "expired":
      return "设备没有响应，没有执行";
    case "unconfirmed":
      return "没有收到结果，上面是设备现在的状态";
  }
}

function Removal({ instance, onRemoved }: { instance: Instance; onRemoved: () => void }) {
  const manage = useManage();
  const [confirming, setConfirming] = useState(false);
  return (
    <Section title="删除实例">
      {confirming ? (
        <div className="border-danger/40 space-y-2 rounded-lg border p-3">
          <p className="text-[13px]">永久删除 {instance.name}？</p>
          <p className="text-fg-3 text-xs leading-relaxed">
            它以前记下的数据保留。以后再安装这个 Observer，会是一个新的程序实例。
          </p>
          <div className="flex justify-end gap-2">
            <Button onClick={() => setConfirming(false)}>取消</Button>
            <Button
              danger
              disabled={manage.isPending}
              onClick={() =>
                manage.mutate(
                  { path: `/observer-instances/${instance.entityId}`, method: "DELETE" },
                  { onSuccess: onRemoved },
                )
              }
            >
              删除
            </Button>
          </div>
        </div>
      ) : (
        <div className="flex items-center gap-2">
          <p className="text-fg-3 flex-1 text-xs">结束这个程序实例的身份。卸载只移除程序，身份保留。</p>
          <Button danger onClick={() => setConfirming(true)}>
            删除…
          </Button>
        </div>
      )}
      {manage.error && <p className="text-danger text-xs">{manage.error.message}</p>}
    </Section>
  );
}
