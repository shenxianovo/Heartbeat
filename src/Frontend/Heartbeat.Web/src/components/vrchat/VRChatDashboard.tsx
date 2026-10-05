"use client";

import { useEffect, useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { useAuth } from "react-oidc-context";
import Link from "next/link";
import { fetchAllRecords } from "@/api/client";
import { useTracksQuery } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { PeriodControls } from "@/components/filters/PeriodControls";
import { useSearchParams } from "next/navigation";
import { readViewRange, recentRange, writeViewRange } from "@/lib/viewRange";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { formatDurationMinutes, rangeToIso, type DateRange } from "@/lib/dates";
import { useObjectScope } from "@/components/objects/ObjectScope";
import { objectHref } from "@/components/objects/model";
import { aggregate } from "./model";
import { EncounterPanel, RhythmPanel, WorldPanel } from "./HabitatPanels";

function useHabitatData(range: DateRange) {
  const scope = useObjectScope();
  const auth = useAuth();
  const token = auth.user?.access_token ?? "";
  const owner = auth.user?.profile.sub ?? "";
  const tracksQuery = useTracksQuery(owner, token, 60000);
  const iso = rangeToIso(range)!;
  const tracks = (tracksQuery.data?.tracks ?? []).filter(
    (track) => track.version === 1 && ["vrchat.location", "vrchat.encounter"].includes(track.type),
  );
  const availableCount = tracks.length;
  const chosen = scope.objectId;
  const queries = useQueries({
    queries: tracks.map((track) => ({
      queryKey: [
        "vrchat",
        owner,
        scope.objectId,
        scope.contextObjectIds,
        track.id,
        iso.from,
        iso.to,
      ],
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        fetchAllRecords(token, { trackId: track.id, ...iso, ...scope }, signal),
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
  const result = aggregate(
    locations,
    encounters,
    Date.parse(iso.from),
    Date.parse(iso.to),
    scope.objectId,
  );
  const fetching = tracksQuery.isFetching || queries.some((query) => query.isFetching);
  const failed = tracksQuery.isError || queries.some((query) => query.isError);
  const loading = tracksQuery.isPending || queries.some((query) => query.isPending);
  const refresh = () => {
    void tracksQuery.refetch();
    queries.forEach((query) => void query.refetch());
  };
  return {
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
  const scope = useObjectScope();
  const [range, setRange] = useState(() => readViewRange(params) ?? recentRange(7));
  useEffect(() => writeViewRange(range), [range]);
  const data = useHabitatData(range);
  const { chosen, result, fetching, failed, refresh } = data;
  return (
    <div>
      <main className="workspace vrc-workspace">
        <div className="vrc-heading">
          <div>
            <h1>世界与相遇</h1>
          </div>
          <Button variant="glass" disabled={fetching} onClick={refresh}>
            {fetching ? "正在刷新" : "刷新"}
          </Button>
        </div>
        <section className="vrc-controls" aria-label="VRChat 筛选">
          <PeriodControls range={range} onChange={setRange} />
        </section>
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
          <Link href={objectHref(chosen!, range, scope.contextObjectIds)}>查看详细时间线</Link> ·{" "}
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
          <strong>{formatDurationMinutes(result.milliseconds)}</strong>
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
