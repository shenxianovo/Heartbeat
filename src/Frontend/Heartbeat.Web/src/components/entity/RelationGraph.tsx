import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { AlertTriangle, Loader2, ZoomIn } from "lucide-react";
import type { EntityCategory } from "@/api/types";
import type { GraphEdge, GraphNode } from "@/lib/graph";
import { layoutRadial } from "@/lib/graph";
import { CATEGORY_STYLE, shortId } from "@/lib/labels";
import { cn } from "@/lib/utils";

interface Props {
  rootId: string;
  nodes: GraphNode[];
  edges: GraphEdge[];
  /** id -> 可读标题（由查询缓存中的实体算出）。 */
  labels: Map<string, string | null>;
  expandableIds: string[];
  onExpand: (ids: string[]) => void;
}

const NODE_R = 34;
const CAT_VAR: Record<EntityCategory, string> = {
  observer: "var(--cat-observer)",
  observation: "var(--cat-observation)",
  entity_schema: "var(--cat-schema)",
  entity: "var(--cat-entity)",
};

export function RelationGraph({ rootId, nodes, edges, labels, expandableIds, onExpand }: Props) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 900, h: 560 });
  const navigate = useNavigate();

  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect;
      if (rect) setSize({ w: Math.max(320, rect.width), h: Math.max(420, rect.height) });
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const positions = useMemo(() => layoutRadial(nodes, edges, size.w, size.h), [nodes, edges, size]);

  const singleEdgeLabel = useMemo(() => {
    const countByFrom = new Map<string, Set<string>>();
    for (const e of edges) {
      const set = countByFrom.get(e.from) ?? new Set<string>();
      set.add(e.to);
      countByFrom.set(e.from, set);
    }
    return (e: GraphEdge) => (countByFrom.get(e.from)?.size === 1 ? e.label : "");
  }, [edges]);

  const go = (id: string) => {
    if (expandableIds.includes(id)) onExpand([id]);
    else void navigate({ to: "/entities/$id", params: { id } });
  };

  return (
    <div ref={wrapRef} className="bg-card/40 relative h-[58vh] min-h-[420px] w-full overflow-hidden rounded-xl border">
      <svg className="absolute inset-0 h-full w-full" aria-hidden="true">
        <defs>
          <pattern id="grid" width="28" height="28" patternUnits="userSpaceOnUse">
            <path d="M 28 0 L 0 0 0 28" fill="none" stroke="var(--border)" strokeWidth="0.6" />
          </pattern>
          <radialGradient id="rootGlow" cx="50%" cy="50%" r="50%">
            <stop offset="0%" stopColor="var(--primary)" stopOpacity="0.18" />
            <stop offset="100%" stopColor="var(--primary)" stopOpacity="0" />
          </radialGradient>
        </defs>
        <rect width="100%" height="100%" fill="url(#grid)" />
      </svg>

      <svg className="absolute inset-0 h-full w-full" role="img" aria-label="实体引用关系图">
        {edges.map((e) => {
          const a = positions.get(e.from);
          const b = positions.get(e.to);
          if (!a || !b) return null;
          const isRootEdge = e.from === rootId;
          const label = singleEdgeLabel(e);
          return (
            <g key={`${e.from}-${e.to}-${e.label}`}>
              <line
                x1={a.x}
                y1={a.y}
                x2={b.x}
                y2={b.y}
                stroke="var(--primary)"
                strokeOpacity={isRootEdge ? 0.55 : 0.26}
                strokeWidth={isRootEdge ? 1.8 : 1.2}
                strokeDasharray={labels.has(e.to) ? undefined : "4 4"}
              />
              {label && (
                <text
                  x={(a.x + b.x) / 2}
                  y={(a.y + b.y) / 2 - 5}
                  textAnchor="middle"
                  className="fill-muted-foreground"
                  fontSize="10"
                >
                  {edgeLabel(label)}
                </text>
              )}
            </g>
          );
        })}

        {positions.get(rootId) && (
          <circle cx={positions.get(rootId)!.x} cy={positions.get(rootId)!.y} r={110} fill="url(#rootGlow)" />
        )}

        {nodes.map((n) => {
          const p = positions.get(n.id);
          if (!p) return null;
          const style = CATEGORY_STYLE[n.category];
          const title = labels.get(n.id);
          const isRoot = n.id === rootId;
          const isExpandable = expandableIds.includes(n.id);
          return (
            <g
              key={n.id}
              transform={`translate(${p.x}, ${p.y})`}
              className="cursor-pointer focus:outline-none"
              onClick={() => go(n.id)}
              onKeyDown={(e) => {
                if (e.key === "Enter") go(n.id);
              }}
              tabIndex={0}
              role="button"
              aria-label={`${style.label} ${title ?? shortId(n.id)}`}
            >
              <circle
                r={isRoot ? NODE_R + 6 : NODE_R}
                fill={n.notFound ? "var(--muted)" : "var(--card)"}
                stroke={n.loading ? "var(--muted-foreground)" : CAT_VAR[n.category]}
                strokeWidth={isRoot ? 3 : 2}
              />
              <circle r={6} cx={0} cy={-9} fill={CAT_VAR[n.category]} opacity={0.95} />
              {n.loading ? (
                <foreignObject x={-10} y={0} width={20} height={20}>
                  <Loader2 className="text-muted-foreground size-5 animate-spin" />
                </foreignObject>
              ) : n.notFound ? (
                <foreignObject x={-10} y={0} width={20} height={20}>
                  <AlertTriangle className="text-muted-foreground size-5" />
                </foreignObject>
              ) : (
                <text textAnchor="middle" y={15} fontSize="11" fontWeight={600} className="fill-foreground">
                  {style.label}
                </text>
              )}
              <text textAnchor="middle" y={NODE_R + 18} fontSize="10.5" className="fill-muted-foreground font-mono">
                {n.notFound ? "不存在" : isExpandable ? "点击加载" : title ? truncate(title, 12) : shortId(n.id)}
              </text>
              {isExpandable && (
                <circle r={NODE_R + 4} fill="none" stroke="var(--primary)" strokeOpacity={0.4} strokeDasharray="3 4">
                  <animate
                    attributeName="r"
                    values={`${NODE_R + 3};${NODE_R + 7};${NODE_R + 3}`}
                    dur="2.4s"
                    repeatCount="indefinite"
                  />
                </circle>
              )}
            </g>
          );
        })}
      </svg>

      <div className="pointer-events-none absolute top-3 left-3 flex flex-wrap items-center gap-3">
        {(["observer", "observation", "entity_schema", "entity"] as const).map((c) => (
          <span
            key={c}
            className="bg-card/80 flex items-center gap-1.5 rounded-full px-2 py-1 text-[11px] backdrop-blur"
          >
            <span className={cn("size-2 rounded-full", styleDot(c))} />
            {CATEGORY_STYLE[c].label}
          </span>
        ))}
      </div>
      <div className="bg-card/80 text-muted-foreground pointer-events-none absolute right-3 bottom-3 flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[11px] backdrop-blur">
        <ZoomIn className="size-3" /> 点击虚线圈加载引用，点击实体跳转
      </div>
    </div>
  );
}

function styleDot(c: EntityCategory): string {
  return CATEGORY_STYLE[c].dot;
}

function truncate(s: string, n: number): string {
  return s.length > n ? `${s.slice(0, n)}…` : s;
}

function edgeLabel(edge: string): string {
  return edge
    .replace(/Id$/, "")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .toLowerCase();
}
