import { useMemo, useState } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import type { Observation, Version } from "@/api/types";
import { typeIcon } from "@/components/token/icons";
import { Mark, Token } from "@/components/token/Token";
import { Card, Message } from "@/components/ui";
import { useEntityCount, type EntityFilter } from "@/hooks/queries";
import { describeVersion } from "@/hooks/useEntityInfo";
import { useTypes, type TypeEdge, type TypeIndex, type TypeNode } from "@/hooks/useTypes";
import { differences, ROLE_NAMES } from "@/lib/roles";
import { DAY } from "@/lib/time";
import { cn } from "@/lib/utils";

interface EntitiesSearch {
  type?: string | undefined;
}

export const Route = createFileRoute("/entities/")({
  validateSearch: (search: Record<string, unknown>): EntitiesSearch =>
    typeof search.type === "string" && search.type ? { type: search.type } : {},
  component: EntitiesPage,
});

/** 列的顺序：来源 ← Observation → 经历 → 事物；InputSet 和 Observation 同列。 */
const COLUMNS: ((node: TypeNode) => boolean)[] = [
  (n) => n.role === "source",
  (n) => n.key === "observation" || n.key === "input_set",
  (n) => n.role === "experience" && n.key !== "observation",
  (n) => n.role === "thing",
];

const NODE_W = 176;
const NODE_H = 40;
const GAP_X = 108;
const GAP_Y = 8;

/** 实体：类型之间怎样引用画成一张图，点一个类型在下面展开它的实体。 */
function EntitiesPage() {
  const types = useTypes();
  const { type } = Route.useSearch();
  const navigate = Route.useNavigate();

  if (types.isPending) return <Message title="正在整理类型" />;
  if (types.isError) return <Message title="读不到类型" detail={types.error.message} mascot />;
  if (types.data.nodes.length <= 2) return <Message title="还没有登记 Schema" mascot />;

  const selected = types.data.nodes.find((node) => node.key === type) ?? null;
  return (
    <section className="grid gap-4">
      <h1 className="text-[19px] font-medium">实体</h1>
      <Card className="overflow-x-auto">
        <TypeMap
          index={types.data}
          selected={selected?.key ?? null}
          onSelect={(key) =>
            void navigate({ search: (prev) => ({ ...prev, type: key === selected?.key ? undefined : key }) })
          }
        />
      </Card>
      {selected && <TypeDetail key={selected.key} node={selected} />}
    </section>
  );
}

function filterOf(node: TypeNode): EntityFilter {
  if (node.key === "observation" || node.key === "input_set") return { category: node.key };
  return { resourceName: node.key };
}

function TypeMap({
  index,
  selected,
  onSelect,
}: {
  index: TypeIndex;
  selected: string | null;
  onSelect: (key: string) => void;
}) {
  const [hover, setHover] = useState<string | null>(null);
  const columns = COLUMNS.map((keep) => ({
    nodes: index.nodes.filter(keep).sort((a, b) => a.name.localeCompare(b.name, "zh")),
  }));
  const position = new Map<string, { x: number; y: number }>();
  columns.forEach((column, c) =>
    column.nodes.forEach((node, r) => position.set(node.key, { x: c * (NODE_W + GAP_X), y: r * (NODE_H + GAP_Y) })),
  );
  const width = columns.length * NODE_W + (columns.length - 1) * GAP_X;
  const height = -GAP_Y + Math.max(...columns.map((c) => c.nodes.length)) * (NODE_H + GAP_Y);
  const focus = hover ?? selected;
  const touches = (edge: TypeEdge) => focus !== null && (edge.from === focus || edge.to === focus);
  const near = new Set(index.edges.filter(touches).flatMap((e) => [e.from, e.to]));
  const showLabels = index.edges.length <= 8;

  return (
    <div className="relative mx-auto" style={{ width, height }}>
      <svg width={width} height={height} className="pointer-events-none absolute inset-0" aria-hidden="true">
        <defs>
          <marker id="type-arrow" viewBox="0 0 6 6" refX="5" refY="3" markerWidth="5" markerHeight="5" orient="auto">
            <path d="M0 0 6 3 0 6Z" fill="var(--line-3)" />
          </marker>
          <marker
            id="type-arrow-lit"
            viewBox="0 0 6 6"
            refX="5"
            refY="3"
            markerWidth="5"
            markerHeight="5"
            orient="auto"
          >
            <path d="M0 0 6 3 0 6Z" fill="var(--accent)" />
          </marker>
        </defs>
        {index.edges.map((edge) => {
          const a = position.get(edge.from);
          const b = position.get(edge.to);
          if (!a || !b) return null;
          const lit = touches(edge);
          const forward = b.x >= a.x;
          const same = b.x === a.x;
          const x1 = forward ? a.x + NODE_W : a.x;
          const x2 = same ? b.x + NODE_W : forward ? b.x : b.x + NODE_W;
          const y1 = a.y + NODE_H / 2;
          const y2 = b.y + NODE_H / 2;
          const bend = same ? 60 : Math.max(30, Math.abs(x2 - x1) / 2);
          const c1 = forward ? x1 + bend : x1 - bend;
          const c2 = same ? x2 + bend : forward ? x2 - bend : x2 + bend;
          const d = `M${x1} ${y1} C${c1} ${y1} ${c2} ${y2} ${x2} ${y2}`;
          return (
            <g key={`${edge.from}>${edge.to}>${edge.label}`} style={{ transition: "opacity 150ms ease-out" }}>
              <path
                d={d}
                fill="none"
                stroke={lit ? "var(--accent)" : "var(--line-3)"}
                strokeOpacity={focus && !lit ? 0.12 : lit ? 0.9 : 0.4}
                strokeWidth={lit ? 1.5 : 1}
                markerEnd={lit ? "url(#type-arrow-lit)" : "url(#type-arrow)"}
              />
              {(lit || (showLabels && !focus)) && (
                <text
                  x={(x1 + x2) / 2}
                  y={(y1 + y2) / 2 - 4}
                  textAnchor="middle"
                  style={{ fontSize: 10, fill: "var(--fg-2)", fontFamily: "var(--font-mono)" }}
                  stroke="var(--card)"
                  strokeWidth={3}
                  paintOrder="stroke"
                >
                  {edge.label.replace(/VersionId(\[\])?$/, "$1")}
                  <title>{edge.label}</title>
                </text>
              )}
            </g>
          );
        })}
      </svg>
      {columns.flatMap((column) =>
        column.nodes.map((node) => {
          const at = position.get(node.key)!;
          return (
            <TypeBox
              key={node.key}
              node={node}
              x={at.x}
              y={at.y}
              selected={selected === node.key}
              dim={focus !== null && focus !== node.key && !near.has(node.key)}
              onHover={setHover}
              onSelect={onSelect}
            />
          );
        }),
      )}
    </div>
  );
}

function TypeBox({
  node,
  x,
  y,
  selected,
  dim,
  onHover,
  onSelect,
}: {
  node: TypeNode;
  x: number;
  y: number;
  selected: boolean;
  dim: boolean;
  onHover: (key: string | null) => void;
  onSelect: (key: string) => void;
}) {
  const count = useEntityCount(filterOf(node));
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={() => onSelect(node.key)}
      onPointerEnter={() => onHover(node.key)}
      onPointerLeave={() => onHover(null)}
      onFocus={() => onHover(node.key)}
      onBlur={() => onHover(null)}
      className={cn(
        "bg-card absolute flex cursor-pointer items-center gap-2 rounded-lg border px-2.5 text-left",
        "transition-[opacity,border-color,box-shadow] duration-150 ease-out",
        selected ? "border-accent ring-accent/25 ring-2" : "border-line-2 hover:border-line-3",
        dim && "opacity-35",
      )}
      style={{ left: x, top: y, width: NODE_W, height: NODE_H }}
    >
      <Mark role={node.role} color={null} icon={typeIcon(node.key)} size="md" />
      <span className="min-w-0 flex-1 truncate text-[13px]">{node.name}</span>
      <span className="text-fg-3 text-xs tabular-nums">
        {count.data ? `${count.data.count}${count.data.more ? "+" : ""}` : ""}
      </span>
    </button>
  );
}

/** 选中类型的实体，按角色排：事物和来源排成记号，经历按时间取最近的。 */
function TypeDetail({ node }: { node: TypeNode }) {
  const count = useEntityCount(filterOf(node));
  return (
    <Card className="rise">
      <div className="mb-3 flex flex-wrap items-start gap-3">
        <Mark role={node.role} color={null} icon={typeIcon(node.key)} size="lg" />
        <div className="min-w-0 flex-1">
          <p className="text-fg-3 text-xs">{ROLE_NAMES[node.role]}</p>
          <h2 className="text-[17px] font-medium">{node.name}</h2>
          {node.schema && <p className="text-fg-2 mt-0.5 text-[13px]">{node.schema.content.description}</p>}
        </div>
        <div className="text-right">
          <p className="text-[22px] leading-none tabular-nums">
            {count.data ? `${count.data.count}${count.data.more ? "+" : ""}` : "…"}
          </p>
          <p className="text-fg-3 mt-1 text-xs">个实体</p>
        </div>
      </div>
      {node.schema && (
        <div className="mb-3 flex items-center gap-1.5 text-[13px]">
          <span className="text-fg-3 text-xs">定义</span>
          <Token version={node.schema} size="sm" />
        </div>
      )}
      {node.role === "experience" ? <RecentExperiences node={node} /> : <EntityTokens node={node} />}
    </Card>
  );
}

function EntityTokens({ node }: { node: TypeNode }) {
  const source = useSource();
  const filter = filterOf(node);
  const list = useQuery({
    queryKey: ["entities", filter, "all"],
    queryFn: () => source.listEntities({ ...filter, limit: 1000 }),
  });
  const items = useMemo(() => {
    const versions = list.data?.versions ?? [];
    const named = versions.map((v) => ({ version: v, title: describeVersion(v, node.schema, node.role).title }));
    named.sort((a, b) => a.title.localeCompare(b.title));
    const groups = new Map<string, number[]>();
    named.forEach((item, i) => groups.set(item.title, [...(groups.get(item.title) ?? []), i]));
    const details = new Array<string | null>(named.length).fill(null);
    for (const members of groups.values()) {
      if (members.length < 2) continue;
      const found = differences(members.map((i) => named[i]!.version.content));
      members.forEach((i, k) => (details[i] = found[k] ?? null));
    }
    return named.map((item, i) => ({ ...item, detail: details[i] }));
  }, [list.data, node.schema, node.role]);

  if (list.isPending) return <p className="text-fg-3 text-[13px]">读取中</p>;
  if (!items.length) return <p className="text-fg-3 text-[13px]">这个类型还没有实体</p>;
  return (
    <ul className="grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-1">
      {items.map((item, i) => (
        <li
          key={item.version.entityId}
          className="rise min-w-0"
          style={{ animationDelay: `${Math.min(i, 20) * 12}ms` }}
        >
          <Token version={item.version} detail={item.detail} />
        </li>
      ))}
    </ul>
  );
}

/** 经历按时间取最近 24 小时里的最后 30 份。 */
function RecentExperiences({ node }: { node: TypeNode }) {
  const source = useSource();
  const [now] = useState(() => Date.now());
  const recent = useQuery({
    queryKey: ["observations", "recent", node.key, Math.floor(now / 60_000)],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({
          from: new Date(now - DAY).toISOString(),
          to: new Date(now + 60_000).toISOString(),
          ...(node.key === "observation" ? {} : { resourceName: node.key }),
          limit: 1000,
          ...(after ? { after } : {}),
        }),
      ) as Promise<Version<Observation>[]>,
  });
  const [shown, setShown] = useState(30);
  const list = (recent.data ?? []).slice(-shown).reverse();
  if (recent.isPending) return <p className="text-fg-3 text-[13px]">读取中</p>;
  if (!list.length) return <p className="text-fg-3 text-[13px]">最近 24 小时没有</p>;
  return (
    <>
      <p className="text-fg-3 mb-1.5 text-xs">最近 24 小时 {recent.data!.length} 份，按时间倒序</p>
      <ul className="grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-1">
        {list.map((observation, i) => (
          <li
            key={observation.entityId}
            className="rise flex min-w-0 items-center gap-1"
            style={{ animationDelay: `${Math.min(i, 20) * 12}ms` }}
          >
            {node.key === "observation" ? (
              <Token version={observation} />
            ) : (
              <Token versionId={observation.content.contentVersionId} />
            )}
          </li>
        ))}
      </ul>
      {recent.data!.length > shown && (
        <button
          type="button"
          onClick={() => setShown((n) => n + 60)}
          className="text-fg-2 hover:text-fg mt-2 cursor-pointer text-xs"
        >
          再显示一些
        </button>
      )}
    </>
  );
}
