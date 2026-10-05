"use client";

import { useQuery } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import { fetchHubs, fetchHubActivity, type HubSummary } from "@/api/hubs";
import { FloatingNavigation } from "@/components/layout/FloatingNavigation";
import { Button } from "@/components/ui/button";
import { Icon } from "@/components/ui/Icon";
import { HubCard } from "./HubCard";

export function HubsPage() {
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const query = useQuery({
    queryKey: ["hubs", auth.user?.profile.sub],
    queryFn: ({ signal }) => fetchHubs(token, signal),
    enabled: !!token,
    refetchInterval: 5000,
  });
  const activity = useQuery({
    queryKey: ["hub-activity", auth.user?.profile.sub],
    queryFn: ({ signal }) => fetchHubActivity(token, signal),
    enabled: !!token,
    refetchInterval: 1000,
    retry: false,
    gcTime: 0,
    structuralSharing: false,
  });
  return (
    <div className="app-shell">
      <FloatingNavigation />
      <main className="hubs-page">
        <HubsHeading
          data={query.data}
          refreshing={query.isFetching}
          refresh={() => void query.refetch()}
        />
        {query.isPending && <p role="status">正在读取 Hub…</p>}
        {query.isError && (
          <p role="alert">无法刷新 Hub 状态：{query.error.message}。连接恢复前登录暂不可用。</p>
        )}
        {query.data?.hubs.length === 0 && (
          <section className="hubs-empty">
            <h2>还没有 Hub 接入</h2>
            <p>启动并配置 Desktop 或服务器上的 Hub，完成连接后会出现在这里。</p>
          </section>
        )}
        {query.data?.hubs.map((hub) => (
          <HubCard
            key={hub.id}
            hub={hub}
            token={token}
            activity={activity.isError ? undefined : activity.data?.activities[hub.id]}
            stale={query.isError}
            refresh={query.refetch}
          />
        ))}
      </main>
    </div>
  );
}

function HubsHeading({
  data,
  refreshing,
  refresh,
}: {
  data?: { hubs: HubSummary[] };
  refreshing: boolean;
  refresh: () => void;
}) {
  return (
    <div className="mb-5 flex items-center justify-between gap-4">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">我的 Hub</h1>
        <p className="mt-1.5 text-sm text-muted-foreground">
          采集与交付状态{data && <span> · {data.hubs.length} 个 Hub</span>}
        </p>
      </div>
      <Button onClick={refresh} disabled={refreshing}>
        <Icon name="refresh" />
        刷新
      </Button>
    </div>
  );
}
