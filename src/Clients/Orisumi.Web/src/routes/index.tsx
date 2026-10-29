import { useEffect, useMemo } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { Timeline } from "@/components/day/Timeline";
import { useHighlight, useStack } from "@/components/token/context";
import { Token } from "@/components/token/Token";
import { Card, Message } from "@/components/ui";
import { productGroup, useCatalog } from "@/hooks/useApplications";
import { useDay } from "@/hooks/useDay";
import { useNow } from "@/hooks/useNow";
import { groupThings, summarize, type Experience, type Row } from "@/lib/day";
import { differences } from "@/lib/roles";
import { dayOf, dayRange, dayTitle, duration, MINUTE } from "@/lib/time";
import { cn } from "@/lib/utils";

interface DaySearch {
  date?: string | undefined;
}

export const Route = createFileRoute("/")({
  validateSearch: (search: Record<string, unknown>): DaySearch =>
    typeof search.date === "string" && dayOf(search.date) ? { date: search.date } : {},
  component: DayPage,
});

/** 一个仍在延长的时段，最后一次写入在这么久以内时画到现在。 */
const ONGOING = 20_000;

/** 日：那一天在用什么、用了多久。左边是排行，右边是时间线；点任何东西都在右边推出它的面板。 */
function DayPage() {
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const now = useNow(1000);
  const todayKey = dayRange(now).key;
  const key = search.date ?? todayKey;
  const day = useDay(key);
  const catalog = useCatalog();
  const { range, today } = day;

  const experiences = useMemo<Experience[]>(() => {
    // 各设备上的同一款软件按应用产品合并成一行。
    const list = catalog.data ? groupThings(day.data?.experiences ?? [], productGroup(catalog.data)) : [];
    if (!today) return list;
    // 今天还在延长的时段画到现在，最后一段跟着时间慢慢变长。
    return list.map((e) => (now - e.end < ONGOING && e.end <= now ? { ...e, end: now } : e));
  }, [day.data, catalog.data, today, now]);
  const summary = useMemo(
    () => summarize(experiences, day.data?.points ?? [], range.start, range.end),
    [experiences, day.data, range.start, range.end],
  );
  const details = useMemo(() => sameNameDetails(summary.things), [summary.things]);

  function go(offset: number) {
    const next = dayRange(range.start + offset * 24 * 60 * MINUTE + 12 * 60 * MINUTE).key;
    void navigate({ search: (prev) => ({ ...prev, date: next === todayKey ? undefined : next }) });
  }

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.target instanceof HTMLInputElement || event.metaKey || event.ctrlKey || event.altKey) return;
      if (event.key === "ArrowLeft") go(-1);
      if (event.key === "ArrowRight" && !today) go(1);
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  const total = summary.things.reduce((sum, row) => sum + row.total, 0);
  const current = today
    ? summary.things.find((row) => row.segments.some((s) => s.end >= now - 1000 && s.start <= now))
    : undefined;
  const writing = today
    ? new Set(
        (day.data?.experiences ?? [])
          .filter((e) => now - Date.parse(e.observation.recordedAt) < 60_000)
          .map((e) => e.observerEntityId),
      ).size
    : 0;

  return (
    <section>
      <header className="mb-4 flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="flex items-center gap-1">
          <button
            type="button"
            aria-label="前一天"
            onClick={() => go(-1)}
            className="text-fg-2 hover:text-fg hover:bg-panel cursor-pointer rounded-md p-1"
          >
            <ChevronLeft className="size-4" />
          </button>
          <h1 className="min-w-[10.5em] text-center text-[19px] font-medium tabular-nums">{dayTitle(range.start)}</h1>
          <button
            type="button"
            aria-label="后一天"
            disabled={today}
            onClick={() => go(1)}
            className="text-fg-2 hover:text-fg hover:bg-panel cursor-pointer rounded-md p-1 disabled:cursor-default disabled:opacity-30"
          >
            <ChevronRight className="size-4" />
          </button>
        </div>
        {!today && (
          <button
            type="button"
            onClick={() => void navigate({ search: (prev) => ({ ...prev, date: undefined }) })}
            className="border-line-2 text-fg-2 hover:text-fg cursor-pointer rounded-md border px-2 py-0.5 text-xs"
          >
            回到今天
          </button>
        )}
        <span className="flex-1" />
        {today && (
          <p className="text-fg-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-[13px]">
            <span className="inline-flex items-center gap-1.5">
              <span className={cn("size-1.5 rounded-full", writing ? "bg-accent animate-pulse" : "bg-fg-3")} />
              {writing ? `${writing} 个 Observer 正在写入` : "没有 Observer 在写入"}
            </span>
            {current && (
              <>
                <span className="text-fg-3">·</span>
                现在在
                <Token version={current.thing!} size="sm" detail={details.get(current.key)} />
              </>
            )}
            <span className="text-fg-3">·</span>
            <Link to="/observers" className="hover:text-fg underline-offset-4 hover:underline">
              Observer 状态
            </Link>
          </p>
        )}
      </header>

      {(day.error && !day.data) || (catalog.error && !catalog.data) ? (
        <Message title="读不到这一天" detail={((day.error ?? catalog.error) as Error).message} mascot />
      ) : (day.loading && !day.data) || !catalog.data ? (
        <Message title="正在读取这一天" />
      ) : !summary.things.length && !summary.lanes.length && !summary.points.length ? (
        <Message title={today ? "今天还没有记录" : "这一天没有记录"} mascot />
      ) : (
        <div className="rise grid gap-4 lg:grid-cols-[260px_minmax(0,1fr)] lg:items-start">
          <Card className="lg:sticky lg:top-4">
            <Overview summary={summary} total={total} />
            <Ranking rows={summary.things} total={total} details={details} />
          </Card>
          <Card className="min-w-0">
            <Timeline summary={summary} from={range.start} to={range.end} now={now} today={today} details={details} />
          </Card>
        </div>
      )}
    </section>
  );
}

function Overview({ summary, total }: { summary: ReturnType<typeof summarize>; total: number }) {
  return (
    <div className="border-line mb-3 border-b pb-3">
      <p className="text-[28px] leading-none font-medium tabular-nums">{duration(total)}</p>
      <p className="text-fg-2 mt-1.5 text-[13px]">
        在 {summary.things.length} 个{summary.thingTypes.length === 1 ? summary.thingTypes[0] : "事物"}之间切换了{" "}
        {summary.switches} 次
      </p>
    </div>
  );
}

/** 当日排行：序号、记号、时长和一条比例条。悬停时时间线上它的段亮起。 */
function Ranking({
  rows,
  total,
  details,
}: {
  rows: Row[];
  total: number;
  details: ReadonlyMap<string, string | null>;
}) {
  const highlight = useHighlight();
  const stack = useStack();
  const max = Math.max(1, ...rows.map((row) => row.total));
  return (
    <ol aria-label="排行" className="-mx-2 grid gap-0.5">
      {rows.map((row, i) => {
        const lit = highlight.active === row.key;
        return (
          <li key={row.key}>
            <div
              className={cn(
                "grid cursor-pointer grid-cols-[1.25rem_minmax(0,1fr)_auto] items-center gap-x-1 rounded-lg px-2 py-1 transition-colors duration-150",
                lit ? "bg-accent-soft" : "hover:bg-panel/70",
              )}
              onPointerEnter={() => highlight.set(row.key)}
              onPointerLeave={() => highlight.set(null)}
              onClick={() => stack.open(row.key, -1)}
            >
              <span className="text-fg-3 text-xs tabular-nums">{i + 1}</span>
              <span className="min-w-0">
                <Token version={row.thing!} size="sm" detail={details.get(row.key)} />
              </span>
              <span className="text-fg-2 text-xs tabular-nums">{duration(row.total)}</span>
              <span />
              <span className="bg-panel col-span-2 mt-0.5 mr-1 ml-1 h-1 overflow-hidden rounded-full">
                <span
                  className="block h-full rounded-full transition-[width] duration-300 ease-out"
                  style={{ width: `${(row.total / max) * 100}%`, background: row.color ?? "var(--fg-3)" }}
                />
              </span>
            </div>
          </li>
        );
      })}
      {total > 0 && <li className="sr-only">共 {duration(total)}</li>}
    </ol>
  );
}

/** 同名的事物（例如同一个 bundle id 的几个安装）后面写出它们不同的那一段。 */
function sameNameDetails(rows: Row[]): Map<string, string | null> {
  const groups = new Map<string, Row[]>();
  for (const row of rows) groups.set(row.title, [...(groups.get(row.title) ?? []), row]);
  const out = new Map<string, string | null>();
  for (const group of groups.values()) {
    if (group.length < 2) continue;
    const found = differences(group.map((row) => row.thing!.content));
    group.forEach((row, i) => out.set(row.key, found[i] ?? null));
  }
  return out;
}
