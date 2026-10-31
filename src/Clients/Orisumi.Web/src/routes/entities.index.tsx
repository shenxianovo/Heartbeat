import { useEffect, useMemo, useState, type ReactNode } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import type { Observation, Version } from "@/api/types";
import { typeIcon } from "@/components/token/icons";
import { InfoMark, Mark, Token } from "@/components/token/Token";
import { Button, Card, Message, PageHeader } from "@/components/ui";
import { WeaveCanvas } from "@/components/weave/Weave";
import { useEntityCount, type EntityFilter } from "@/hooks/queries";
import { describeVersion, useDescribe, useEntityInfo } from "@/hooks/useEntityInfo";
import { useEntity } from "@/hooks/queries";
import { useTypes, type TypeEdge, type TypeIndex, type TypeNode } from "@/hooks/useTypes";
import { referenceLabel } from "@/lib/references";
import { differences, ROLE_NAMES } from "@/lib/roles";
import { DAY } from "@/lib/time";
import { cn } from "@/lib/utils";
import { isUuidv7 } from "@/lib/uuid";

interface EntitiesSearch {
  type?: string | undefined;
  /** 织图的起点，EntityId。 */
  root?: string | undefined;
}

export const Route = createFileRoute("/entities/")({
  validateSearch: (search: Record<string, unknown>): EntitiesSearch => ({
    ...(typeof search.type === "string" && search.type ? { type: search.type } : {}),
    ...(typeof search.root === "string" && isUuidv7(search.root) ? { root: search.root } : {}),
  }),
  component: EntitiesPage,
});

/** 列的顺序：来源 ← Observation → 经历 → 事物；InputSet 和 Observation 同列。 */
const COLUMNS: { label: string; keep: (node: TypeNode) => boolean }[] = [
  { label: "来源", keep: (n) => n.role === "source" },
  { label: "观测", keep: (n) => n.key === "observation" || n.key === "input_set" },
  { label: "经历", keep: (n) => n.role === "experience" && n.key !== "observation" },
  { label: "事物", keep: (n) => n.role === "thing" },
];

/**
 * 分列的图随卡片宽度伸缩：列距至少放得下最长的连线标签，宽度不够时先收窄节点，
 * 节点收到最窄还放不下就改成上下排列，不画连线。
 */
const MIN_NODE_W = 140;
const MAX_NODE_W = 176;
const NODE_H = 40;
const GAP_Y = 8;
const MIN_GAP_X = 56;
const MAX_GAP_X = 160;
/** 连线标签是 10px 等宽字，每个字符约 6px；标签离节点 6px，另留 6px 给箭头。 */
const LABEL_CHAR_W = 6;
const LABEL_PAD = 12;
const LABEL_STEP = 12;

/**
 * 实体：有起点时是织图，从起点一层层长出和它有引用关系的实体。
 * 没有起点时是类型图，点一个类型列出它的实体，点实体从它开始织。
 */
function EntitiesPage() {
  const types = useTypes();
  const { type, root } = Route.useSearch();
  const navigate = Route.useNavigate();

  if (root)
    return (
      <section>
        <PageHeader
          title="实体"
          summary={
            <p>
              从 <RootName entityId={root} />{" "}
              开始织。点节点长出和它有引用关系的实体，再点收起；停在节点上，看它是怎么长出来的。
            </p>
          }
          actions={<Button onClick={() => void navigate({ search: (prev) => ({ type: prev.type }) })}>换个起点</Button>}
        />
        <WeaveCanvas key={root} rootId={root} />
      </section>
    );

  if (types.isPending) return <Message title="正在整理类型" />;
  if (types.isError) return <Message title="读不到类型" detail={types.error.message} mascot />;
  if (types.data.nodes.length <= 2) return <Message title="还没有登记 Schema" mascot />;

  const selected = types.data.nodes.find((node) => node.key === type) ?? null;
  return (
    <section>
      <PageHeader
        title="实体"
        summary={`${types.data.nodes.length} 种类型。线上写着引用它的字段。点一个类型列出它的实体，再点一个实体从它开始织。`}
      />
      <div className="grid gap-4">
        <Card className="min-w-0">
          <TypeMap
            index={types.data}
            selected={selected?.key ?? null}
            onSelect={(key) =>
              void navigate({ search: (prev) => ({ ...prev, type: key === selected?.key ? undefined : key }) })
            }
          />
        </Card>
        {selected && <TypeDetail key={selected.key} node={selected} />}
      </div>
    </section>
  );
}

function filterOf(node: TypeNode): EntityFilter {
  if (node.key === "observation" || node.key === "input_set") return { category: node.key };
  return { resourceName: node.key };
}

/** 节点在分列的图里的位置和宽度。 */
interface Place {
  x: number;
  y: number;
  w: number;
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
  const [box, setBox] = useState<HTMLDivElement | null>(null);
  const [width, setWidth] = useState(0);
  useEffect(() => {
    if (!box) return;
    const observer = new ResizeObserver(([entry]) => setWidth(Math.round(entry!.contentRect.width)));
    observer.observe(box);
    return () => observer.disconnect();
  }, [box]);

  const columns = COLUMNS.map(({ label, keep }) => ({
    label,
    nodes: index.nodes.filter(keep).sort((a, b) => a.name.localeCompare(b.name, "zh")),
  }));
  const focus = hover ?? selected;
  const touches = (edge: TypeEdge) => focus !== null && (edge.from === focus || edge.to === focus);
  const near = new Set(index.edges.filter(touches).flatMap((e) => [e.from, e.to]));
  const renderBox = (node: TypeNode, at?: Place) => (
    <TypeBox
      key={node.key}
      node={node}
      at={at}
      selected={selected === node.key}
      dim={focus !== null && focus !== node.key && !near.has(node.key)}
      onHover={setHover}
      onSelect={onSelect}
    />
  );

  const n = columns.length;
  const longest = Math.max(0, ...index.edges.map((edge) => referenceLabel(edge.label).length));
  const minGap = Math.max(MIN_GAP_X, longest * LABEL_CHAR_W + LABEL_PAD);
  const nodeW = Math.min(MAX_NODE_W, (width - (n - 1) * minGap) / n);
  const fits = nodeW >= MIN_NODE_W;
  return (
    <div ref={setBox}>
      {width === 0 ? null : fits ? (
        <TypeGraph
          columns={columns}
          edges={index.edges}
          nodeW={nodeW}
          gap={Math.min(MAX_GAP_X, (width - n * nodeW) / (n - 1))}
          focus={focus}
          touches={touches}
          renderBox={renderBox}
        />
      ) : (
        <div className="grid gap-3">
          {columns
            .filter((column) => column.nodes.length)
            .map((column) => (
              <div key={column.label}>
                <p className="text-fg-3 mb-1.5 text-xs">{column.label}</p>
                <div className="grid grid-cols-[repeat(auto-fill,minmax(140px,1fr))] gap-2">
                  {column.nodes.map((node) => renderBox(node))}
                </div>
              </div>
            ))}
        </div>
      )}
    </div>
  );
}

/**
 * 分列的类型图。同一个类型从同一侧连出的同名引用只标一次，标在出发的一端；
 * 一个类型在同一侧有几种引用时，标签向上错开。
 */
function TypeGraph({
  columns,
  edges,
  nodeW,
  gap,
  focus,
  touches,
  renderBox,
}: {
  columns: { nodes: TypeNode[] }[];
  edges: TypeEdge[];
  nodeW: number;
  gap: number;
  focus: string | null;
  touches: (edge: TypeEdge) => boolean;
  renderBox: (node: TypeNode, at: Place) => ReactNode;
}) {
  const position = new Map<string, Place>();
  columns.forEach((column, c) =>
    column.nodes.forEach((node, r) =>
      position.set(node.key, { x: c * (nodeW + gap), y: r * (NODE_H + GAP_Y), w: nodeW }),
    ),
  );
  const width = columns.length * nodeW + (columns.length - 1) * gap;
  const height = -GAP_Y + Math.max(...columns.map((c) => c.nodes.length)) * (NODE_H + GAP_Y);

  const labels = new Map<string, { from: string; right: boolean; label: string; edges: TypeEdge[] }>();
  for (const edge of edges) {
    const a = position.get(edge.from);
    const b = position.get(edge.to);
    if (!a || !b) continue;
    const right = b.x >= a.x;
    const key = `${edge.from}>${right}>${edge.label}`;
    const group = labels.get(key) ?? { from: edge.from, right, label: edge.label, edges: [] };
    group.edges.push(edge);
    labels.set(key, group);
  }
  const stacked = new Map<string, number>();
  const showLabels = labels.size <= 8;

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
        {edges.map((edge) => {
          const a = position.get(edge.from);
          const b = position.get(edge.to);
          if (!a || !b) return null;
          const lit = touches(edge);
          const forward = b.x >= a.x;
          const same = b.x === a.x;
          const x1 = forward ? a.x + nodeW : a.x;
          const x2 = same ? b.x + nodeW : forward ? b.x : b.x + nodeW;
          const y1 = a.y + NODE_H / 2;
          const y2 = b.y + NODE_H / 2;
          const bend = same ? 60 : Math.max(30, Math.abs(x2 - x1) / 2);
          const c1 = forward ? x1 + bend : x1 - bend;
          const c2 = same ? x2 + bend : forward ? x2 - bend : x2 + bend;
          const d = `M${x1} ${y1} C${c1} ${y1} ${c2} ${y2} ${x2} ${y2}`;
          return (
            <path
              key={`${edge.from}>${edge.to}>${edge.label}`}
              d={d}
              fill="none"
              stroke={lit ? "var(--accent)" : "var(--line-3)"}
              strokeOpacity={focus && !lit ? 0.12 : lit ? 0.9 : 0.4}
              strokeWidth={lit ? 1.5 : 1}
              markerEnd={lit ? "url(#type-arrow-lit)" : "url(#type-arrow)"}
              style={{ transition: "opacity 150ms ease-out" }}
            />
          );
        })}
        {[...labels.entries()].map(([key, group]) => {
          const a = position.get(group.from)!;
          const side = `${group.from}>${group.right}`;
          const row = stacked.get(side) ?? 0;
          stacked.set(side, row + 1);
          const lit = group.edges.some(touches);
          if (!lit && !(showLabels && !focus)) return null;
          return (
            <text
              key={key}
              x={group.right ? a.x + nodeW + 6 : a.x - 6}
              y={a.y + NODE_H / 2 - 4 - row * LABEL_STEP}
              textAnchor={group.right ? "start" : "end"}
              style={{ fontSize: 10, fill: "var(--fg-2)", fontFamily: "var(--font-mono)" }}
              stroke="var(--card)"
              strokeWidth={3}
              paintOrder="stroke"
            >
              {referenceLabel(group.label)}
              <title>{group.label}</title>
            </text>
          );
        })}
      </svg>
      {columns.flatMap((column) => column.nodes.map((node) => renderBox(node, position.get(node.key)!)))}
    </div>
  );
}

function TypeBox({
  node,
  at,
  selected,
  dim,
  onHover,
  onSelect,
}: {
  node: TypeNode;
  /** 在分列的图里的位置；不给时随父元素排列。 */
  at?: Place | undefined;
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
        "bg-card flex cursor-pointer items-center gap-2 rounded-lg border px-2.5 text-left",
        "transition-[opacity,border-color,box-shadow] duration-150 ease-out",
        at ? "absolute" : "w-full",
        selected ? "border-accent ring-accent/25 ring-2" : "border-line-2 hover:border-line-3",
        dim && "opacity-35",
      )}
      style={at ? { left: at.x, top: at.y, width: at.w, height: NODE_H } : { height: NODE_H }}
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
          <StartButton version={item.version} detail={item.detail} />
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
              <StartButton version={observation} />
            ) : (
              <StartButton versionId={observation.content.contentVersionId} />
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

/** 起点的名字。 */
function RootName({ entityId }: { entityId: string }) {
  const entity = useEntity(entityId);
  const { info } = useDescribe(entity.data);
  return <span className="text-fg font-medium">「{info?.title ?? "…"}」</span>;
}

/** 列表里的一个实体：记号和名字，点它从它开始织。 */
function StartButton({
  version,
  versionId,
  detail,
}: {
  version?: Version;
  versionId?: string;
  detail?: string | null | undefined;
}) {
  const byId = useEntityInfo(version ? null : versionId);
  const given = useDescribe(version);
  const { info } = version ? given : byId;
  const navigate = Route.useNavigate();
  if (!info) return <span className="bg-panel block h-7 rounded-md" />;
  return (
    <button
      type="button"
      title={`从 ${info.title} 开始织`}
      onClick={() => void navigate({ search: (prev) => ({ ...prev, root: info.version.entityId }) })}
      className="hover:bg-panel flex w-full min-w-0 cursor-pointer items-center gap-1.5 rounded-md px-1.5 py-0.5 text-left text-[13px]"
    >
      <InfoMark info={info} size="md" />
      <span className="min-w-0 truncate">
        {info.title}
        {detail && <span className="text-fg-3 font-mono text-[11px]"> {detail}</span>}
      </span>
    </button>
  );
}
