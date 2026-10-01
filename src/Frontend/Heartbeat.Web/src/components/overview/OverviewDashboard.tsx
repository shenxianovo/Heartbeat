"use client";

import { useState } from "react";
import Link from "next/link";
import { AppHeader } from "@/components/layout/AppHeader";
import { PeriodControls } from "@/components/filters/PeriodControls";
import { Button, buttonVariants } from "@/components/ui/button";
import { LoadingState } from "@/components/status/LoadingState";
import { QueryState } from "@/components/status/QueryState";
import { BubbleMap } from "@/components/visualizations/BubbleMap";
import { detailHref, recentRange } from "@/lib/viewRange";
import { formatDurationMinutes, type DateRange } from "@/lib/dates";
import { useOverviewData } from "./useOverviewData";

type Data = ReturnType<typeof useOverviewData>;

export function OverviewDashboard() {
  const [range, setRange] = useState(() => recentRange(7));
  const data = useOverviewData(range);
  return (
    <div className="app-frame">
      <AppHeader />
      <main className="workspace overview-workspace">
        <div className="overview-heading">
          <div>
            <span className="track-source">YOUR DIGITAL LIFE</span>
            <h1>最近的足迹</h1>
            <p>从一幅概览开始，走进每一段记录。</p>
          </div>
          <Button variant="glass" disabled={data.fetching} onClick={data.refresh}>
            {data.fetching ? "正在刷新" : "刷新"}
          </Button>
        </div>
        <section className="overview-controls" aria-label="概览时间范围">
          <PeriodControls range={range} onChange={setRange} />
        </section>
        <OverviewContent data={data} range={range} />
        <Link className="glass-panel overview-timeline" href={detailHref("/timeline", range)}>
          <div>
            <span className="track-source">EXPLORE THE DETAILS</span>
            <h2>详细时间线</h2>
            <p>展开泳道，查看各来源的具体记录与时间顺序。</p>
          </div>
          <span aria-hidden="true">↗</span>
        </Link>
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
  if (!data.sources.length)
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
      <SourceMap data={data} range={range} />
      {data.sources.some((source) => !source.track) ? (
        <section className="overview-other" aria-label="其他来源">
          <h2>其他记录</h2>
          <p>这些来源暂未生成时长概览，可以直接查看详细记录。</p>
          {data.sources
            .filter((source) => !source.track)
            .map((source) => (
              <Link key={source.id} href={detailHref("/timeline", range, source.id)}>
                {source.name} ↗
              </Link>
            ))}
        </section>
      ) : null}
    </>
  );
}

function SourceMap({ data, range }: { data: Data; range: DateRange }) {
  const [list, setList] = useState(false);
  const items = data.summaries
    .map((source, index) => ({
      ...source,
      detail: source.pending ? "正在读取" : formatDurationMinutes(source.milliseconds),
      weight: source.milliseconds,
      color:
        source.kind === "vrchat" ? "oklch(0.79 0.1 205)" : `oklch(0.79 0.1 ${265 + index * 45})`,
      href: detailHref(source.kind === "vrchat" ? "/vrchat" : "/timeline", range, source.id),
      accessibleLabel: `${source.label} · ${source.name}，查看详情`,
    }))
    .sort((a, b) => b.weight - a.weight);
  if (!items.length) return null;
  return (
    <section className="glass-panel overview-map" aria-labelledby="overview-map-title">
      <div className="section-heading">
        <div>
          <span className="track-source">ACTIVITY MAP</span>
          <h2 id="overview-map-title">你的数字生活</h2>
        </div>
        <Button variant="glass" onClick={() => setList(!list)}>
          {list ? "气泡视图" : "列表视图"}
        </Button>
      </div>
      <p className="overview-note">
        每个气泡代表一个来源，面积近似表示已确认的记录时长。点击进入详情。
      </p>
      {!list ? <BubbleMap label="活动概览气泡图" items={items.slice(0, 24)} /> : null}
      <div className="overview-sources" aria-label="来源摘要">
        {items.map((source) => (
          <SourceSummary key={source.id} source={source} />
        ))}
      </div>
      {!list && items.length > 24 ? (
        <p className="overview-note">气泡展示时长最多的 24 个来源，列表保留全部来源。</p>
      ) : null}
      <p className="overview-note">
        各来源可能覆盖同一段时间，时长分别展示，不累加为总时长。桌面按前台应用观测，VRChat
        按可见世界停留统计；观测空白不补齐。
      </p>
    </section>
  );
}

function SourceSummary({ source }: { source: Data["summaries"][number] & { href: string } }) {
  return (
    <Link className="overview-source" href={source.href}>
      <div>
        <strong>{source.label}</strong>
        <span>{source.name}</span>
      </div>
      <div>
        {source.pending ? (
          <span>正在读取</span>
        ) : (
          <strong>{formatDurationMinutes(source.milliseconds)}</strong>
        )}
        <span>
          {source.subjects} 个{source.kind === "vrchat" ? "世界" : "应用"} · 查看详情 ↗
        </span>
      </div>
      {source.failed ? <p role="alert">读取失败，当前数据可能不完整。请刷新重试。</p> : null}
      {source.invalid ? <p role="alert">{source.invalid} 条记录无法解析，已排除。</p> : null}
    </Link>
  );
}
