"use client";

import { useQuery } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import { fetchHubs } from "@/api/hubs";
import { AppHeader } from "@/components/layout/AppHeader";
import { Button } from "@/components/ui/button";
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
  return (
    <div className="app-shell">
      <AppHeader />
      <main className="hubs-page">
        <div className="hub-heading">
          <div>
            <h1>我的 Hub</h1>
            <p>查看各处采集与交付状态，管理在线 Hub 的 Collector。</p>
          </div>
          <Button onClick={() => void query.refetch()} disabled={query.isFetching}>
            刷新
          </Button>
        </div>
        {query.isPending && <p role="status">正在读取 Hub…</p>}
        {query.isError && (
          <p role="alert">无法刷新 Hub 状态：{query.error.message}。连接恢复前管理操作暂不可用。</p>
        )}
        {query.data?.hubs.length === 0 && (
          <section className="hub-card">
            <h2>还没有 Hub 接入</h2>
            <p>启动并配置 Desktop 或服务器上的 Hub，完成连接后会出现在这里。</p>
          </section>
        )}
        {query.data?.hubs.map((hub) => (
          <HubCard
            key={hub.id}
            hub={hub}
            token={token}
            stale={query.isError}
            refresh={query.refetch}
          />
        ))}
      </main>
    </div>
  );
}
