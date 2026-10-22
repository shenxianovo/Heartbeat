import type { EntityCategory } from "@/api/types";

/** 引用图节点：以实体 ID 为键，缓存已读取的实体信封。 */
export interface GraphNode {
  id: string;
  category: EntityCategory;
  /** 从哪个节点的哪个引用展开而来（根节点为 null）。 */
  discoveredFrom: { fromId: string; edge: string } | null;
  loading?: boolean;
  notFound?: boolean;
}

export interface GraphEdge {
  from: string;
  to: string;
  label: string;
}

/** 把一组已加载实体展平成节点与边。 */
export function buildGraph(
  rootId: string,
  entities: Map<string, { category: EntityCategory; references: Record<string, string | null> }>,
  missing: ReadonlySet<string>,
): { nodes: GraphNode[]; edges: GraphEdge[] } {
  const nodes = new Map<string, GraphNode>();
  const edges: GraphEdge[] = [];
  const seenEdges = new Set<string>();

  const visit = (id: string, source: GraphNode["discoveredFrom"]) => {
    if (!nodes.has(id)) {
      const ent = entities.get(id);
      nodes.set(id, {
        id,
        category: ent?.category ?? "entity",
        discoveredFrom: source,
        loading: !ent && !missing.has(id),
        notFound: missing.has(id),
      });
    }
    const ent = entities.get(id);
    if (!ent) return;
    for (const [edge, target] of Object.entries(ent.references)) {
      if (!target) continue;
      const key = `${id}->${target}:${edge}`;
      if (!seenEdges.has(key)) {
        seenEdges.add(key);
        edges.push({ from: id, to: target, label: edge });
      }
      if (!nodes.has(target)) visit(target, { fromId: id, edge });
    }
  };

  visit(rootId, null);
  return { nodes: [...nodes.values()], edges };
}

/** 极简径向/力导向布局：BFS 分层 + 同层均布圆；根在中心。 */
export function layoutRadial(nodes: GraphNode[], edges: GraphEdge[], width: number, height: number) {
  if (nodes.length === 0) return new Map<string, { x: number; y: number }>();
  const adj = new Map<string, string[]>();
  for (const n of nodes) adj.set(n.id, []);
  for (const e of edges) adj.get(e.from)?.push(e.to);

  const root = nodes[0]!.id;
  const depth = new Map<string, number>([[root, 0]]);
  const queue = [root];
  while (queue.length > 0) {
    const cur = queue.shift()!;
    const d = depth.get(cur)!;
    for (const next of adj.get(cur) ?? []) {
      if (!depth.has(next)) {
        depth.set(next, d + 1);
        queue.push(next);
      }
    }
  }

  const cx = width / 2;
  const cy = height / 2;
  const maxDepth = Math.max(1, ...depth.values());
  const layerGap = Math.min(width, height) / (maxDepth + 1) / 1.6;
  const byLayer = new Map<number, string[]>();
  for (const [id, d] of depth) {
    const list = byLayer.get(d) ?? [];
    list.push(id);
    byLayer.set(d, list);
  }

  const pos = new Map<string, { x: number; y: number }>();
  pos.set(root, { x: cx, y: cy });
  for (const [d, ids] of byLayer) {
    if (d === 0) continue;
    const radius = d * layerGap;
    ids.forEach((id, i) => {
      const angle = (i / ids.length) * Math.PI * 2 - Math.PI / 2;
      pos.set(id, { x: cx + radius * Math.cos(angle), y: cy + radius * Math.sin(angle) });
    });
  }
  return pos;
}
