"use client";

import { useState } from "react";
import Link from "next/link";
import { FloatingNavigation } from "@/components/layout/FloatingNavigation";
import { PeriodControls } from "@/components/filters/PeriodControls";
import { Button, buttonVariants } from "@/components/ui/button";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { SourceRows } from "./SourceRows";
import { recentRange } from "@/lib/viewRange";
import { type DateRange } from "@/lib/dates";
import { useOverviewData } from "./useOverviewData";

type Data = ReturnType<typeof useOverviewData>;

export function OverviewDashboard() {
  const [range, setRange] = useState(() => recentRange(7));
  const data = useOverviewData(range);
  return (
    <div className="app-frame">
      <FloatingNavigation />
      <main className="workspace overview-workspace">
        <section className="overview-controls" aria-label="概览时间范围">
          <PeriodControls range={range} onChange={setRange} />
          <Link className={buttonVariants({ variant: "glass" })} href="/objects">
            所有对象
          </Link>
          <Button variant="glass" disabled={data.fetching} onClick={data.refresh}>
            {data.fetching ? "正在刷新" : "刷新"}
          </Button>
        </section>
        <OverviewContent data={data} range={range} />
      </main>
    </div>
  );
}

function OverviewContent({ data, range }: { data: Data; range: DateRange }) {
  if (data.catalog.isPending) return <LoadingState label="正在读取概览" />;
  if (data.catalog.isError)
    return (
      <QueryState
        eyebrow="读取失败"
        title="暂时无法读取概览"
        description="采集来源暂时不可用，请稍后重试。"
        action={<Button onClick={data.refresh}>重试</Button>}
      />
    );
  if (!data.catalog.data?.objects.length)
    return (
      <QueryState
        eyebrow="从第一段记录开始"
        title="还没有可以展示的足迹"
        description="连接采集来源并完成上传后，你的概览会出现在这里。"
        action={
          <Link className={buttonVariants({ variant: "glass" })} href="/hubs">
            管理采集来源
          </Link>
        }
      />
    );
  return (
    <>
      <SourceRows sources={data.summaries} range={range} />
      {data.sources.some((source) => source.kind === "vrchat") ? (
        <section className="glass-panel overview-other">
          <Link
            href={`/objects?namespace=vrchat.account&role=account&from=${encodeURIComponent(range.from)}&to=${encodeURIComponent(range.to)}`}
          >
            VRChat ↗
          </Link>
          <span>{data.sources.filter((source) => source.kind === "vrchat").length} 个账号</span>
        </section>
      ) : null}
    </>
  );
}
