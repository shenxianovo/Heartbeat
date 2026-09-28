"use client";

import { useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import Link from "next/link";
import { fetchAllRecords } from "@/api/client";
import type { TrackSummary } from "@/api/types";
import { useTracksQuery } from "@/api/queries";
import { AppHeader } from "@/components/layout/AppHeader";
import { Button } from "@/components/ui/button";
import { PeriodControls } from "@/components/filters/PeriodControls";
import { useSearchParams } from "next/navigation";
import { detailHref, readViewRange, recentRange } from "@/lib/viewRange";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { rangeToIso, type DateRange } from "@/lib/dates";
import { aggregate, duration } from "./model";
import { EncounterPanel, RhythmPanel, WorldPanel } from "./HabitatPanels";

function selectSources(allTracks: TrackSummary[], collectorId: string) {
  const available = allTracks.filter(
    (track) => track.version === 1 && ["vrchat.location", "vrchat.encounter"].includes(track.type),
  );
  const collectors = [
    ...new Map(available.map((track) => [track.collectorId, track.collectorDisplayName])).entries(),
  ];
  const chosen = collectors.some(([id]) => id === collectorId) ? collectorId : collectors[0]?.[0];
  const tracks = available.filter((track) => track.collectorId === chosen);
  return { availableCount: available.length, collectors, chosen, tracks };
}

function useHabitatData(collectorId: string, range: DateRange) {
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const owner = auth.user?.profile.sub ?? "";
  const tracksQuery = useTracksQuery(owner, token, 60000);
  const iso = rangeToIso(range)!;
  const { availableCount, collectors, chosen, tracks } = selectSources(
    tracksQuery.data?.tracks ?? [],
    collectorId,
  );
  const queries = useQueries({
    queries: tracks.map((track) => ({
      queryKey: ["vrchat", owner, track.id, iso.from, iso.to],
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        fetchAllRecords(token, { trackId: track.id, ...iso }, signal),
      enabled: Boolean(token),
      refetchInterval: 60000,
    })),
  });
  const locations = queries.flatMap((query, index) =>
    tracks[index]!.type === "vrchat.location" ? (query.data?.records ?? []) : [],
  );
  const encounters = queries.flatMap((query, index) =>
    tracks[index]!.type === "vrchat.encounter" ? (query.data?.records ?? []) : [],
  );
  const result = aggregate(locations, encounters, Date.parse(iso.from), Date.parse(iso.to));
  const fetching = tracksQuery.isFetching || queries.some((query) => query.isFetching);
  const failed = tracksQuery.isError || queries.some((query) => query.isError);
  const loading = tracksQuery.isPending || queries.some((query) => query.isPending);
  const refresh = () => {
    void tracksQuery.refetch();
    queries.forEach((query) => void query.refetch());
  };
  return {
    collectors,
    chosen,
    result,
    fetching,
    failed,
    loading,
    availableCount,
    refresh,
  };
}

export function VRChatDashboard() {
  const params = useSearchParams();
  const [collectorId, setCollectorId] = useState(() => params.get("collector") ?? "");
  const [range, setRange] = useState(() => readViewRange(params) ?? recentRange(7));
  const data = useHabitatData(collectorId, range);
  const { collectors, chosen, result, fetching, failed, refresh } = data;
  return (
    <div className="app-frame">
      <AppHeader />
      <main className="workspace vrc-workspace">
        <nav className="page-breadcrumb" aria-label="当前位置">
          <Link href="/">概览</Link>
          <span>/</span>
          <span>VRChat</span>
        </nav>
        <div className="vrc-heading">
          <div>
            <span className="track-source">VRCHAT · YOUR HABITAT</span>
            <h1>世界与相遇</h1>
            <p>去过的世界，以及与你处于同一实例的可见好友。</p>
          </div>
          <Button variant="glass" disabled={fetching} onClick={refresh}>
            {fetching ? "正在刷新" : "刷新"}
          </Button>
        </div>
        <section className="vrc-controls" aria-label="VRChat 筛选">
          <PeriodControls range={range} onChange={setRange} />
          {collectors.length > 0 ? (
            <label className="vrc-source">
              账号来源
              <select value={chosen} onChange={(event) => setCollectorId(event.target.value)}>
                {collectors.map(([id, name]) => (
                  <option key={id} value={id}>
                    {name} · {id.slice(-6)}
                  </option>
                ))}
              </select>
            </label>
          ) : null}
        </section>
        <p className="vrc-observation-note">
          服务器 API 可见观测 · 约每五分钟核对一次 ·
          断线和隐藏位置保留为空白，末段只统计到最近一次确认。
        </p>
        {failed ? (
          <div className="vrc-error" role="alert">
            部分数据读取失败，当前汇总可能不完整。
            <Button variant="ghost" onClick={refresh}>
              重试
            </Button>
          </div>
        ) : null}
        {result.invalid ? (
          <p className="vrc-error" role="alert">
            {result.invalid} 条记录无法解析，已从汇总中排除。
          </p>
        ) : null}
        <HabitatContent key={`${chosen}/${range.from}/${range.to}`} data={data} />
        <footer className="vrc-caption">
          观测段数可能因断线拆分，不等同于真实访问次数。
          <Link href={detailHref("/timeline", range, chosen)}>查看详细时间线</Link> ·{" "}
          <Link href="/hubs">查看 Hub 采集状态</Link>
        </footer>
      </main>
    </div>
  );
}

function HabitatContent({ data }: { data: ReturnType<typeof useHabitatData> }) {
  const { loading, availableCount, result, chosen } = data;
  return loading ? (
    <LoadingState label="正在读取世界与相遇记录" />
  ) : !availableCount ? (
    <QueryState
      eyebrow="尚无记录"
      title="从你的第一个世界开始"
      description="在服务器 Hub 登录 VRChat 后，位置与可见同场记录会出现在这里。"
    />
  ) : !result.worlds.length ? (
    <QueryState
      eyebrow="这个时间范围暂无位置记录"
      title="还没有可以展示的世界"
      description="换一个时间范围，或在 VRChat 进入世界后等待采集上传。"
    />
  ) : (
    <>
      <div className="vrc-stats">
        <div>
          <span>世界停留</span>
          <strong>
            {duration(result.worlds.reduce((sum, world) => sum + world.milliseconds, 0))}
          </strong>
        </div>
        <div>
          <span>到访世界</span>
          <strong>{result.worlds.length}</strong>
        </div>
        <div>
          <span>可见同场好友</span>
          <strong>{result.people.length}</strong>
        </div>
      </div>
      <WorldPanel key={chosen} worlds={result.worlds} />
      <RhythmPanel worlds={result.worlds} />
      <EncounterPanel people={result.people} />
    </>
  );
}
