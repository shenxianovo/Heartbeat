import { createFileRoute } from "@tanstack/react-router";
import { ObserverRows } from "@/components/observers/ObserverRows";
import { Card, Message } from "@/components/ui";
import { useActivity } from "@/hooks/useActivity";
import { useNow } from "@/hooks/useNow";
import { isActive, observerRows } from "@/lib/activity";
import { clock } from "@/lib/time";

export const Route = createFileRoute("/observers")({
  component: ObserversPage,
});

/** 每个 Observer 今天什么时候在记录，现在是否在写入。 */
function ObserversPage() {
  const now = useNow(1000);
  const activity = useActivity(now);
  const rows = activity.loading ? [] : observerRows({ ...activity, now });
  const active = rows.filter((row) => isActive(row, now)).length;
  const count = rows.reduce((sum, row) => sum + row.count, 0);
  const latest = rows.reduce<(typeof rows)[number] | null>(
    (best, row) => (row.lastAt > (best?.lastAt ?? 0) ? row : best),
    null,
  );

  return (
    <section>
      <header aria-label="现在的状态" className="mb-4 flex flex-wrap items-baseline gap-x-4 gap-y-1">
        <p aria-live="polite" className="text-[15px]">
          {activity.loading ? "正在读取 Observer" : `${rows.length} 个 Observer，${active} 个正在写入`}
        </p>
        {!activity.loading && (
          <p className="text-fg-3 text-xs tabular-nums">
            今天 {count.toLocaleString()} 份 Observation
            {latest && ` · 最后一次写入 ${clock(latest.lastAt)}，${latest.name}`}
          </p>
        )}
      </header>
      <Card>
        {activity.error && !rows.length ? (
          <Message title="读不到数据" detail={activity.error.message} />
        ) : activity.loading ? (
          <Message title="读取中" />
        ) : !rows.length ? (
          <Message title="还没有 Observer" detail="Observer 写入第一份 Observation 后出现在这里" />
        ) : (
          <ObserverRows rows={rows} day={activity.day} now={now} />
        )}
      </Card>
    </section>
  );
}
