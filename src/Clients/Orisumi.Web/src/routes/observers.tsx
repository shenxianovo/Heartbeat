import { useEffect } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { ActivityView, TITLES } from "@/components/ActivityView";
import { NowSummary } from "@/components/NowSummary";
import { Card, Dot, Message, Segmented } from "@/components/ui";
import { useActivity } from "@/hooks/useActivity";
import { useNow, useReducedMotion } from "@/hooks/useNow";
import { CATEGORIES, categoryInfo } from "@/lib/categories";
import { isView, readView, saveView, type ViewKey } from "@/lib/preferences";
import { ago } from "@/lib/time";

interface NowSearch {
  view?: ViewKey | undefined;
  observer?: string | undefined;
}

export const Route = createFileRoute("/observers")({
  validateSearch: (search: Record<string, unknown>): NowSearch => ({
    ...(isView(search.view) ? { view: search.view } : {}),
    ...(typeof search.observer === "string" && search.observer ? { observer: search.observer } : {}),
  }),
  component: NowPage,
});

const VIEW_OPTIONS = [
  { value: "bubble", label: "气泡" },
  { value: "strip", label: "行" },
  { value: "ring", label: "表盘" },
] as const;

function NowPage() {
  const search = Route.useSearch();
  const navigate = Route.useNavigate();
  const view = search.view ?? readView();
  const selected = search.observer ?? null;
  const now = useNow(1000);
  const reduced = useReducedMotion();
  const { store, day, loading, error } = useActivity(now);
  const snapshot = store.snapshot(now);
  const observers = snapshot.observers;
  const chosen = observers.find((o) => o.key === selected) ?? null;

  useEffect(() => saveView(view), [view]);

  const setSearch = (next: NowSearch) => navigate({ search: (prev) => ({ ...prev, ...next }), replace: true });

  return (
    <section>
      <NowSummary
        now={now}
        dayStart={day.start}
        observers={observers}
        observationCount={snapshot.observationCount}
        loading={loading}
        selected={chosen}
      />
      <Card>
        <div className="mb-2 flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-fg-2 text-[13px]">{TITLES[view]}</h2>
          <Segmented
            label="视图"
            options={VIEW_OPTIONS}
            value={view}
            onChange={(value) => setSearch({ view: value })}
          />
        </div>
        {error && !observers.length ? (
          <Message title="读不到数据" detail={error.message} />
        ) : !loading && snapshot.observationCount === 0 ? (
          <Message title="今天还没有记录" />
        ) : (
          <ActivityView
            view={view}
            store={store}
            dayStart={day.start}
            selected={selected}
            reduced={reduced}
            onSelect={(key) => setSearch({ observer: key ?? undefined })}
          />
        )}
        <footer className="border-line text-fg-2 mt-2.5 flex min-h-[22px] flex-wrap items-center gap-3.5 border-t pt-2.5 text-xs">
          {chosen ? (
            <>
              <Dot color={categoryInfo(chosen.category).color} />
              <span className="text-fg">{chosen.name}</span>
              <span>{categoryInfo(chosen.category).label}</span>
              <span>10 分钟 {chosen.writes.length} 次写入</span>
              <span>{ago(now - chosen.lastActivityAt)}</span>
              <span className="flex-1" />
              {chosen.entityId && (
                <Link
                  to="/entities/$entityId"
                  params={{ entityId: chosen.entityId }}
                  className="text-fg border-line-2 border-b"
                >
                  打开实体
                </Link>
              )}
            </>
          ) : (
            CATEGORIES.filter((c) => observers.some((o) => o.category === c.key)).map((c) => (
              <span key={c.key} className="inline-flex items-center gap-1.5">
                <Dot color={c.color} />
                {c.label}
              </span>
            ))
          )}
        </footer>
      </Card>
    </section>
  );
}
