import { useEffect, useMemo } from "react";
import { Link, createFileRoute, useNavigate } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { AlertCircle, Boxes, Clock3, Network, RefreshCw } from "lucide-react";
import { entityQueryKey } from "@/api/queries";
import { api } from "@/api/client";
import { CATEGORY_LABEL } from "@/api/types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { RelationGraph } from "@/components/entity/RelationGraph";
import { InspectCard } from "@/components/entity/InspectCard";
import { PulseTimeline } from "@/components/entity/PulseTimeline";
import { toObservationItem } from "@/lib/observations";
import { useReferenceGraph } from "@/lib/useReferenceGraph";
import { useEntityHistory, useSchemaBookmarks } from "@/lib/storage";
import { entityLabel } from "@/lib/labels";

export const Route = createFileRoute("/entities/$id")({
  component: EntityWorkspace,
  validateSearch: (search: Record<string, unknown>): { view?: "graph" | "inspect" | "pulse" } => ({
    view: search.view === "inspect" || search.view === "pulse" ? search.view : "graph",
  }),
});

type View = "graph" | "inspect" | "pulse";

function EntityWorkspace() {
  const { id } = Route.useParams();
  const { view } = Route.useSearch();
  const navigate = useNavigate();
  const history = useEntityHistory();
  const bookmarks = useSchemaBookmarks();

  const rootQuery = useQuery({
    queryKey: entityQueryKey(id),
    queryFn: () => api.getEntity(id),
    retry: false,
  });

  const { graph, entities, expandableIds, expand } = useReferenceGraph(rootQuery.data ? id : null);

  useEffect(() => {
    if (rootQuery.data) {
      history.push({ id, category: rootQuery.data.category, label: entityLabel(rootQuery.data, id) });
    }
    // 仅在 id 或实体首次到达时记录足迹。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, rootQuery.data]);

  const labels = useMemo(() => {
    const map = new Map<string, string | null>();
    for (const [eid, entity] of entities) {
      map.set(eid, entityLabel(entity, eid));
    }
    return map;
  }, [entities]);

  const observations = useMemo(
    () =>
      [...entities.entries()]
        .filter(([, e]) => e.category === "observation")
        .map(([oid, e]) => toObservationItem(oid, e)),
    [entities],
  );

  const isBookmarked = bookmarks.ids.includes(id);

  const setView = (next: View) => {
    void navigate({ to: "/entities/$id", params: { id }, search: { view: next } });
  };

  return (
    <div className="grid gap-5">
      <div className="flex flex-wrap items-center gap-3">
        <Link to="/" className="text-muted-foreground hover:text-foreground text-sm">
          ← 首页
        </Link>
        <span className="text-muted-foreground">/</span>
        <span className="text-muted-foreground font-mono text-xs">{id}</span>
        <div className="ml-auto flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={() => void rootQuery.refetch()}>
            <RefreshCw className={`size-3.5 ${rootQuery.isFetching ? "animate-spin" : ""}`} />
            刷新
          </Button>
        </div>
      </div>

      {rootQuery.isLoading && (
        <div className="grid gap-3">
          <Skeleton className="h-9 w-48" />
          <Skeleton className="h-[58vh] w-full rounded-xl" />
        </div>
      )}

      {rootQuery.isError && (
        <div className="glass mx-auto mt-10 grid max-w-lg gap-4 rounded-2xl p-8 text-center">
          <span className="bg-destructive/10 mx-auto flex size-12 items-center justify-center rounded-2xl">
            <AlertCircle className="text-destructive size-6" />
          </span>
          <div>
            <h1 className="text-lg font-semibold">无法读取这个实体</h1>
            <p className="text-muted-foreground mt-1 text-sm break-words">{rootQuery.error.message}</p>
            <p className="text-muted-foreground mt-2 font-mono text-xs">{id}</p>
          </div>
          <Button asChild variant="outline">
            <Link to="/">换一个 ID 重新观测</Link>
          </Button>
        </div>
      )}

      {rootQuery.data && (
        <>
          <div className="flex flex-wrap items-center gap-3">
            <Badge className="text-sm">{CATEGORY_LABEL[rootQuery.data.category]}</Badge>
            <h1 className="text-xl font-bold tracking-tight">{entityLabel(rootQuery.data, id) ?? "未命名实体"}</h1>
            {rootQuery.data.category === "entity_schema" && (
              <Button
                variant="ghost"
                size="sm"
                className="ml-auto"
                onClick={() => (isBookmarked ? bookmarks.remove(id) : bookmarks.add(id))}
              >
                {isBookmarked ? "取消收藏定义" : "收藏此定义"}
              </Button>
            )}
          </div>

          <Tabs value={view} onValueChange={(v) => setView(v as View)}>
            <TabsList className="grid w-full max-w-md grid-cols-3">
              <TabsTrigger value="graph">
                <Network className="size-4" /> 关系图
              </TabsTrigger>
              <TabsTrigger value="inspect">
                <Boxes className="size-4" /> 检视
              </TabsTrigger>
              <TabsTrigger value="pulse">
                <Clock3 className="size-4" /> Pulse
              </TabsTrigger>
            </TabsList>

            <div className="mt-4">
              {view === "graph" && (
                <RelationGraph
                  rootId={id}
                  nodes={graph.nodes}
                  edges={graph.edges}
                  labels={labels}
                  expandableIds={expandableIds}
                  onExpand={(newIds) => expand(newIds)}
                />
              )}
              {view === "inspect" && (
                <InspectCard
                  id={id}
                  entity={rootQuery.data}
                  bookmarked={isBookmarked}
                  onToggleBookmark={() => (isBookmarked ? bookmarks.remove(id) : bookmarks.add(id))}
                />
              )}
              {view === "pulse" && (
                <div className="grid gap-4">
                  <PulseTimeline
                    observations={observations}
                    selectedId={id}
                    onSelect={(oid) =>
                      void navigate({ to: "/entities/$id", params: { id: oid }, search: { view: "pulse" } })
                    }
                  />
                  <p className="text-muted-foreground text-xs">
                    Pulse 聚合当前关系图中已加载的 {observations.length} 条观测；点击脉冲可跳转到该观测。
                  </p>
                </div>
              )}
            </div>
          </Tabs>
        </>
      )}
    </div>
  );
}
