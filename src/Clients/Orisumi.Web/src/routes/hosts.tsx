import { useState } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { Plus } from "lucide-react";
import { runState, synced, type Host } from "@/api/management";
import { HostCard } from "@/components/hosts/HostCard";
import { InstallDialog, PairingDialog } from "@/components/hosts/dialogs";
import { InstancePanel } from "@/components/hosts/InstancePanel";
import { Button, Card, Message, PageHeader } from "@/components/ui";
import { useHosts, useInstances } from "@/hooks/useManagement";
import { useNow } from "@/hooks/useNow";

export const Route = createFileRoute("/hosts")({ component: HostsPage });

/** 每台设备和服务器上的程序实例：启停、设置、维护和删除，以及接入新的机器。 */
export function HostsPage() {
  const now = useNow(1000);
  const hosts = useHosts();
  const instances = useInstances();
  const [selected, setSelected] = useState<string>();
  const [pairing, setPairing] = useState<{ host: Host | null }>();
  const [installing, setInstalling] = useState<Host>();

  const all = instances.data ?? [];
  const list = [...(hosts.data ?? [])].sort((a, b) => Number(a.revoked) - Number(b.revoked));
  const live = list.filter((host) => !host.revoked);
  const offline = live.filter((host) => !host.connected).length;
  const running = all.filter((instance) => {
    const host = list.find((item) => item.hostId === instance.hostId);
    return host && !instance.removed && runState(host, instance) === "running";
  }).length;
  const waiting = all.filter((instance) => !instance.removed && !synced(instance)).length;
  const instance = all.find((item) => item.entityId === selected);
  const host = list.find((item) => item.hostId === instance?.hostId);
  const error = hosts.error ?? instances.error;

  return (
    <section>
      <PageHeader
        title="托管"
        summary={
          <>
            <p aria-live="polite" className="text-fg">
              {hosts.isPending
                ? "正在读取"
                : `${live.length} 台机器${offline ? `，${offline} 台离线` : live.length ? "，都在线" : ""}`}
            </p>
            {!hosts.isPending && live.length > 0 && (
              <p className="tabular-nums">
                {running} 个 Observer 在运行{waiting ? ` · ${waiting} 个等待设备同步` : ""}
              </p>
            )}
          </>
        }
        actions={
          <Button primary onClick={() => setPairing({ host: null })}>
            <Plus className="size-3.5" />
            接入
          </Button>
        }
      />
      {error && !hosts.data ? (
        <Card>
          <Message title="读不到托管方" detail={error.message} />
        </Card>
      ) : hosts.isPending ? null : !list.length ? (
        <Card>
          <Message mascot title="还没有接入的机器" detail="接入一台设备或服务器，就能在这里管理它上面的 Observer。">
            <div className="mt-2">
              <Button primary onClick={() => setPairing({ host: null })}>
                接入设备或服务器
              </Button>
            </div>
          </Message>
        </Card>
      ) : (
        <div className="grid gap-3">
          {list.map((item) => (
            <HostCard
              key={item.hostId}
              host={item}
              instances={all.filter((entry) => entry.hostId === item.hostId)}
              now={now}
              selected={selected}
              onSelect={(entityId) => setSelected(entityId === selected ? undefined : entityId)}
              onInstall={() => setInstalling(item)}
              onPair={() => setPairing({ host: item })}
            />
          ))}
        </div>
      )}
      {instance && host && (
        <InstancePanel
          key={instance.entityId}
          host={host}
          instance={instance}
          now={now}
          onClose={() => setSelected(undefined)}
        />
      )}
      {pairing && <PairingDialog host={pairing.host} now={now} onClose={() => setPairing(undefined)} />}
      {installing && (
        <InstallDialog
          host={installing}
          instances={all.filter((entry) => entry.hostId === installing.hostId)}
          onClose={() => setInstalling(undefined)}
        />
      )}
    </section>
  );
}
