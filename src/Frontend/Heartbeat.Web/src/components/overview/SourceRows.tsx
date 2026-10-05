import { useId } from "react";
import Link from "next/link";
import { formatDurationMinutes, type DateRange } from "@/lib/dates";
import { objectHref } from "@/components/objects/model";
import type { useOverviewData } from "./useOverviewData";

type Source = ReturnType<typeof useOverviewData>["summaries"][number];
const dayLabel = new Intl.DateTimeFormat("zh-CN", { month: "2-digit", day: "2-digit" });

function DailyBars({ days, maximum }: { days: Source["days"]; maximum: number }) {
  const description = useId();
  const labelEvery = Math.max(1, Math.ceil(days.length / 3));
  const readings = days.map(
    (day) =>
      `${dayLabel.format(day.from)}：${day.milliseconds > 0 ? formatDurationMinutes(day.milliseconds) : "无记录"}`,
  );
  return (
    <div
      className="source-days"
      role="img"
      aria-label="每日时长"
      aria-describedby={description}
      style={{ gridTemplateColumns: `repeat(${days.length}, minmax(0, 1fr))` }}
    >
      {days.map((day, index) => (
        <div className="source-day" key={day.from} title={readings[index]}>
          {day.milliseconds > 0 ? (
            <span
              className="source-day-bar"
              style={{ height: `${Math.max(1, (day.milliseconds / maximum) * 48)}px` }}
            />
          ) : null}
          {index % labelEvery === 0 ? (
            <span className="source-day-label">{dayLabel.format(day.from)}</span>
          ) : null}
        </div>
      ))}
      <span id={description} className="sr-only">
        {readings.join("；")}
      </span>
    </div>
  );
}

function SourceTotal({ source }: { source: Source }) {
  const unavailable = source.pending || source.failed;
  return (
    <div className="source-total">
      <strong>
        {unavailable
          ? "—"
          : source.milliseconds > 0
            ? formatDurationMinutes(source.milliseconds)
            : "暂无记录"}
      </strong>
      {!unavailable ? (
        <small>
          {source.subjects} 个{source.kind === "vrchat" ? "世界" : "应用"}
        </small>
      ) : null}
    </div>
  );
}

function SourceRow({
  source,
  range,
  maximum,
}: {
  source: Source;
  range: DateRange;
  maximum: number;
}) {
  const unavailable = source.pending || source.failed;
  return (
    <article className="overview-source">
      <div className="source-identity">
        <Link
          href={objectHref(source.id, range)}
          aria-label={`${source.label} · ${source.name}，查看详情`}
        >
          {source.name} <span aria-hidden="true">↗</span>
        </Link>
        <small>{source.label}</small>
      </div>
      {source.summaryAvailable || unavailable ? (
        <>
          <div className="source-chart">
            {unavailable ? (
              <p className="source-chart-status" role={source.failed ? "alert" : undefined}>
                {source.pending ? "正在读取每日记录…" : "读取失败，请刷新重试。"}
              </p>
            ) : (
              <DailyBars days={source.days} maximum={maximum} />
            )}
          </div>
          <SourceTotal source={source} />
        </>
      ) : null}
      {source.invalid ? (
        <p role="alert" className="source-warning">
          {source.invalid} 条记录无法解析。
        </p>
      ) : null}
    </article>
  );
}

export function SourceRows({ sources, range }: { sources: Source[]; range: DateRange }) {
  if (!sources.length) return null;
  const maximum = sources.reduce(
    (max, source) =>
      source.failed
        ? max
        : source.days.reduce((value, day) => Math.max(value, day.milliseconds), max),
    3_600_000,
  );
  return (
    <section className="glass-panel overview-map" aria-labelledby="overview-map-title">
      <div className="section-heading">
        <h2 id="overview-map-title">每日记录</h2>
      </div>
      <div className="overview-sources" aria-label="对象摘要">
        {sources.map((source) => (
          <SourceRow key={source.id} source={source} range={range} maximum={maximum} />
        ))}
      </div>
    </section>
  );
}
