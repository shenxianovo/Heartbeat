import { useMemo } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { DayNav } from "@/components/day/DayNav";
import { Timeline } from "@/components/day/Timeline";
import { Card, Message } from "@/components/ui";
import { productGroup, useCatalog } from "@/hooks/useApplications";
import { useDay } from "@/hooks/useDay";
import { useNow } from "@/hooks/useNow";
import { groupThings, summarize, type Experience, type Row } from "@/lib/day";
import { differences } from "@/lib/roles";
import { dayOf, dayRange, duration } from "@/lib/time";

interface DaySearch {
  date?: string | undefined;
}

export const Route = createFileRoute("/")({
  validateSearch: (search: Record<string, unknown>): DaySearch =>
    typeof search.date === "string" && dayOf(search.date) ? { date: search.date } : {},
  component: DayPage,
});

/** 日：那一天在用什么、用了多久。上面是总时长，下面是排行和时间线合在一起的表；点任何东西都在右边推出它的面板。 */
function DayPage() {
  const search = Route.useSearch();
  const now = useNow(1000);
  const todayKey = dayRange(now).key;
  const key = search.date ?? todayKey;
  const day = useDay(key);
  const catalog = useCatalog();
  const { range, today } = day;

  const experiences = useMemo<Experience[]>(() => {
    // 各设备上的同一款软件按应用产品合并成一行。
    return catalog.data ? groupThings(day.data?.experiences ?? [], productGroup(catalog.data)) : [];
  }, [day.data, catalog.data]);
  const summary = useMemo(
    () => summarize(experiences, day.data?.points ?? [], range.start, range.end),
    [experiences, day.data, range.start, range.end],
  );
  const details = useMemo(() => sameNameDetails(summary.things), [summary.things]);

  const total = summary.things.reduce((sum, row) => sum + row.total, 0);
  return (
    <section>
      <header className="mb-6 flex flex-wrap items-end gap-x-8 gap-y-4">
        <div className="min-w-[min(100%,320px)] flex-1">
          <DayNav start={range.start} today={today} />
          {summary.things.length > 0 && <Overview summary={summary} total={total} />}
        </div>
      </header>

      {(day.error && !day.data) || (catalog.error && !catalog.data) ? (
        <Message title="读不到这一天" detail={((day.error ?? catalog.error) as Error).message} mascot />
      ) : (day.loading && !day.data) || !catalog.data ? (
        <Message title="正在读取这一天" />
      ) : !summary.things.length && !summary.lanes.length && !summary.points.length ? (
        <Message title={today ? "今天还没有记录" : "这一天没有记录"} mascot />
      ) : (
        <Card className="rise min-w-0 px-4 py-4 sm:px-5">
          <Timeline summary={summary} from={range.start} to={range.end} now={now} today={today} details={details} />
        </Card>
      )}
    </section>
  );
}

/** 这一天的总时长和切换次数，写在日期下面。 */
function Overview({ summary, total }: { summary: ReturnType<typeof summarize>; total: number }) {
  return (
    <p className="mt-3 flex flex-wrap items-baseline gap-x-3 gap-y-1">
      <span className="text-[40px] leading-none font-semibold tracking-[-0.02em] tabular-nums">{duration(total)}</span>
      <span className="text-fg-2 text-[13px]">
        在 {summary.things.length} 个{summary.thingTypes.length === 1 ? summary.thingTypes[0] : "事物"}之间切换了{" "}
        {summary.switches} 次
      </span>
    </p>
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
