import { Link } from "@tanstack/react-router";
import { ArrowUpRight, Bookmark, BookmarkCheck, Clock3 } from "lucide-react";
import type { EntityResponse } from "@/api/types";
import { CATEGORY_LABEL } from "@/api/types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { formatDateTime, formatDuration } from "@/lib/time";
import { shortId } from "@/lib/labels";

interface Props {
  id: string;
  entity: EntityResponse;
  bookmarked: boolean;
  onToggleBookmark: () => void;
}

export function InspectCard({ id, entity, bookmarked, onToggleBookmark }: Props) {
  const refs = Object.entries(entity.references);
  const propsEntries = Object.entries(entity.properties);

  return (
    <Card>
      <CardHeader className="gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <Badge>{CATEGORY_LABEL[entity.category]}</Badge>
          {entity.schemaId && <Badge variant="secondary">schemaId 可见</Badge>}
          <Button variant="ghost" size="sm" className="ml-auto h-7 gap-1.5 text-xs" onClick={onToggleBookmark}>
            {bookmarked ? <BookmarkCheck className="size-3.5" /> : <Bookmark className="size-3.5" />}
            {bookmarked ? "已收藏定义" : "收藏为定义"}
          </Button>
        </div>
        <CardTitle className="text-muted-foreground font-mono text-sm break-all" title={id}>
          {id}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <Tabs defaultValue="table">
          <TabsList className="w-full max-w-xs">
            <TabsTrigger value="table">结构化</TabsTrigger>
            <TabsTrigger value="json">原始 JSON</TabsTrigger>
          </TabsList>

          <TabsContent value="table" className="grid gap-5">
            {entity.category === "observation" && <ObservationTime entity={entity} />}

            <section className="grid gap-2">
              <h3 className="eyebrow">references · {refs.length}</h3>
              {refs.length === 0 ? (
                <p className="text-muted-foreground text-sm">{} —— 该实体没有具名引用。</p>
              ) : (
                <ul className="grid gap-1.5">
                  {refs.map(([key, value]) => (
                    <li
                      key={key}
                      className="border-border/60 bg-background/40 flex flex-wrap items-center gap-2 rounded-lg border px-3 py-2"
                    >
                      <span className="text-muted-foreground font-mono text-xs">{key}</span>
                      {value ? (
                        <Link
                          to="/entities/$id"
                          params={{ id: value }}
                          className="group text-primary ml-auto flex max-w-full items-center gap-1.5 font-mono text-xs hover:underline"
                        >
                          <span className="truncate">{shortId(value)}</span>
                          <ArrowUpRight className="size-3.5 shrink-0 transition-transform group-hover:translate-x-0.5 group-hover:-translate-y-0.5" />
                        </Link>
                      ) : (
                        <Badge variant="outline" className="ml-auto">
                          null（显式空值）
                        </Badge>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </section>

            <Separator />

            <section className="grid gap-2">
              <h3 className="eyebrow">properties · {propsEntries.length}</h3>
              <PropertyTable entries={propsEntries} />
            </section>
          </TabsContent>

          <TabsContent value="json">
            <pre className="bg-background/60 max-h-[52vh] overflow-auto rounded-lg border p-4 font-mono text-xs leading-relaxed">
              {JSON.stringify(entity, null, 2)}
            </pre>
          </TabsContent>
        </Tabs>
      </CardContent>
    </Card>
  );
}

function PropertyTable({ entries }: { entries: [string, unknown][] }) {
  if (entries.length === 0) return <p className="text-muted-foreground text-sm">{} —— 没有其他属性。</p>;
  return (
    <ul className="grid gap-1.5">
      {entries.map(([key, value]) => (
        <li
          key={key}
          className="border-border/60 bg-background/40 grid gap-1 rounded-lg border px-3 py-2 md:grid-cols-[200px_1fr] md:gap-4"
        >
          <span className="text-muted-foreground font-mono text-xs">{key}</span>
          <PropertyValue value={value} />
        </li>
      ))}
    </ul>
  );
}

function PropertyValue({ value }: { value: unknown }) {
  if (value === null) return <Badge variant="outline">null</Badge>;
  if (value === undefined) return <Badge variant="outline">字段省略（未提交）</Badge>;
  if (typeof value === "string") return <span className="text-sm break-all">{value}</span>;
  if (typeof value === "number" || typeof value === "boolean")
    return <span className="font-mono text-sm">{String(value)}</span>;
  return (
    <pre className="text-muted-foreground overflow-auto font-mono text-xs leading-relaxed">
      {JSON.stringify(value, null, 2)}
    </pre>
  );
}

function ObservationTime({ entity }: { entity: EntityResponse }) {
  const start = entity.properties.startAt;
  const end = entity.properties.endAt;
  const tz = entity.properties.timeZone;
  return (
    <section className="border-cat-observation/30 bg-cat-observation/8 rounded-xl border p-4">
      <div className="text-cat-observation flex items-center gap-2">
        <Clock3 className="size-4" />
        <h3 className="text-sm font-semibold">事实时间</h3>
      </div>
      <div className="mt-3 grid gap-2 text-sm md:grid-cols-3">
        <div>
          <p className="eyebrow mb-0.5">startAt</p>
          <p className="tabular">{formatDateTime(typeof start === "string" ? start : null)}</p>
        </div>
        <div>
          <p className="eyebrow mb-0.5">endAt</p>
          <p className="tabular flex items-center gap-2">
            {formatDateTime(typeof end === "string" ? end : null)}
            {end === null && <span className="animate-pulse-dot bg-ongoing inline-block size-2 rounded-full" />}
          </p>
        </div>
        <div>
          <p className="eyebrow mb-0.5">时长 / 时区</p>
          <p className="tabular">
            {typeof start === "string" ? formatDuration(start, typeof end === "string" ? end : null) : "—"}
            {typeof tz === "string" && tz ? ` · ${tz}` : " · 时区未知"}
          </p>
        </div>
      </div>
    </section>
  );
}
