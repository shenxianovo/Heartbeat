import type { InputSet, Observation, Version } from "@/api/types";

// 织图：从一个实体出发，点一个节点，和它有关的实体从两边长出来。
// 实体是线，Observation 是把线系在一起的结：谁（Observer）依据什么（InputSet 的输入）说了什么（观测内容）。
// - 观测内容是经历的，Observation 并进经历的节点，节点写出事实时间和 Observer。
// - 其余的 Observation 画成结；它的 InputSet 并进结，输入是结左边的线。
// 位置按来龙去脉从左往右：Observer 和输入在陈述左边，陈述在它的内容左边，业务字段的引用方在被引用的实体左边。
// 只用 Core 的结构，新的 Observer 和类型接入后不用改这里。

export const NODE_W = 208;
export const NODE_H = 52;
export const GAP_Y = 14;
/** 相邻两栏的距离，中间留出连线标签的位置。 */
export const COL_STEP = NODE_W + 120;
/** 同一组邻居超过这个数时，多出来的收成一束。 */
export const BUNDLE_AT = 6;
/** 收成一束时，直接长出来的个数；点一次线束再放出的个数。 */
export const SHOW_FIRST = 3;
export const REVEAL = 8;

/** 节点画的东西：一个实体（经历连同记下它的 Observation），或者一个结。 */
export type Payload =
  | { kind: "entity"; version: Version; observation: Version<Observation> | null }
  | { kind: "knot"; observation: Version<Observation>; inputSet: Version<InputSet> | null };

export function payloadKey(payload: Payload): string {
  return payload.kind === "entity" ? payload.version.entityId : payload.observation.entityId;
}

/** 邻居在哪一边：left 在来龙去脉的上游，right 在下游。 */
export type Side = "left" | "right";

/** 还没解析成节点的邻居：读到的版本、在哪一边、经由什么，以及分组和排序用的类型名和时间。 */
export interface Neighbor {
  version: Version;
  side: Side;
  /** 连线上写的字：来源、依据、内容，或者业务字段名，例如 `localApplication`。 */
  label: string;
  typeName: string;
  time: number;
}

export interface ItemNode {
  kind: "entity" | "knot";
  key: string;
  payload: Payload;
  col: number;
  y: number;
  /** 从哪个节点长出来；起点为 null。 */
  parent: string | null;
  expanded: boolean;
}

export interface BundleNode {
  kind: "bundle";
  key: string;
  col: number;
  y: number;
  parent: string;
  side: Side;
  label: string;
  typeName: string;
  /** 还没放出来的成员，按时间从近到远。 */
  members: Neighbor[];
}

export type WeaveNode = ItemNode | BundleNode;

/** 一条线，from 是长出 to 的节点。 */
export interface WeaveEdge {
  from: string;
  to: string;
  label: string;
}

export interface Weave {
  nodes: WeaveNode[];
  edges: WeaveEdge[];
}

export function start(payload: Payload): Weave {
  return {
    nodes: [{ kind: payload.kind, key: payloadKey(payload), payload, col: 0, y: 0, parent: null, expanded: false }],
    edges: [],
  };
}

/** 同一边、同一个标签、同一类型的邻居是一组。数组下标不算在标签里。 */
function groupKey(neighbor: Neighbor): string {
  return `${neighbor.side}|${plainLabel(neighbor.label)}|${neighbor.typeName}`;
}

function plainLabel(label: string): string {
  return label.replace(/\[\d+\]/g, "[]");
}

/** 把邻居分成直接长出来的和收进线束的：每组超过 BUNDLE_AT 个时，只直接长出最近的 SHOW_FIRST 个。 */
export function plan(neighbors: readonly Neighbor[]): { direct: Neighbor[]; bundles: Neighbor[][] } {
  const groups = new Map<string, Neighbor[]>();
  for (const neighbor of neighbors) {
    const key = groupKey(neighbor);
    groups.set(key, [...(groups.get(key) ?? []), neighbor]);
  }
  const direct: Neighbor[] = [];
  const bundles: Neighbor[][] = [];
  for (const members of groups.values()) {
    members.sort((a, b) => b.time - a.time);
    if (members.length > BUNDLE_AT) {
      direct.push(...members.slice(0, SHOW_FIRST));
      bundles.push(members.slice(SHOW_FIRST));
    } else direct.push(...members);
  }
  return { direct, bundles };
}

/** 解析好的邻居：它画成的节点，以及它在哪一边、经由什么连过来。 */
export interface Resolved {
  payload: Payload;
  side: Side;
  label: string;
}

/**
 * 从一个节点长出它的邻居。direct 是已经解析的，bundles 是收进线束的组。
 * 已经在图上的只连一条线；解析后指向同一个节点的只算一次。
 */
export function grow(weave: Weave, key: string, direct: readonly Resolved[], bundles: readonly Neighbor[][]): Weave {
  const parent = weave.nodes.find((node) => node.key === key);
  if (!parent || parent.kind === "bundle") return weave;
  const nodes = weave.nodes.map((node) => (node.key === key ? { ...node, expanded: true } : node));
  const next = attach({ nodes, edges: weave.edges }, parent, direct);
  const added: BundleNode[] = bundles.map((members) => {
    const first = members[0]!;
    return {
      kind: "bundle",
      key: `bundle:${key}:${groupKey(first)}`,
      col: parent.col + (first.side === "right" ? 1 : -1),
      y: parent.y,
      parent: key,
      side: first.side,
      label: plainLabel(first.label),
      typeName: first.typeName,
      members,
    };
  });
  return {
    nodes: spread([...next.nodes, ...added], new Set([...next.added, ...added.map((node) => node.key)])),
    edges: [...next.edges, ...added.map((bundle) => ({ from: key, to: bundle.key, label: bundle.label }))],
  };
}

/** 线束里接下来要放出的成员。 */
export function nextFromBundle(weave: Weave, bundleKey: string): Neighbor[] {
  const bundle = weave.nodes.find((node) => node.key === bundleKey);
  return bundle?.kind === "bundle" ? bundle.members.slice(0, REVEAL) : [];
}

/** 放出线束的下一批成员，挂在线束的父节点下面；放完时线束和它的线一起消失。 */
export function reveal(weave: Weave, bundleKey: string, resolved: readonly Resolved[]): Weave {
  const bundle = weave.nodes.find((node) => node.key === bundleKey);
  if (!bundle || bundle.kind !== "bundle") return weave;
  const parent = weave.nodes.find((node) => node.key === bundle.parent);
  if (!parent || parent.kind === "bundle") return weave;
  const rest = bundle.members.slice(REVEAL);
  const base: Weave = {
    nodes: weave.nodes.flatMap((node) =>
      node.key !== bundleKey ? [node] : rest.length ? [{ ...bundle, members: rest }] : [],
    ),
    edges: weave.edges.filter((edge) => rest.length || edge.to !== bundleKey),
  };
  // 新节点从线束的位置排开。
  const next = attach(base, parent, resolved, bundle.y);
  return { nodes: spread(next.nodes, next.added), edges: next.edges };
}

/** 把解析好的邻居挂到 parent 上：新节点放进 parent 左右的栏，已有的只连线。 */
function attach(weave: Weave, parent: ItemNode, resolved: readonly Resolved[], y = parent.y) {
  const present = new Set(weave.nodes.map((node) => node.key));
  const linked = new Set(weave.edges.map((edge) => pairKey(edge.from, edge.to)));
  const nodes = [...weave.nodes];
  const edges = [...weave.edges];
  const added = new Set<string>();
  for (const { payload, side, label } of resolved) {
    const key = payloadKey(payload);
    if (key === parent.key) continue;
    if (!present.has(key)) {
      nodes.push({
        kind: payload.kind,
        key,
        payload,
        col: parent.col + (side === "right" ? 1 : -1),
        y,
        parent: parent.key,
        expanded: false,
      });
      present.add(key);
      added.add(key);
    }
    if (!linked.has(pairKey(parent.key, key))) {
      edges.push({ from: parent.key, to: key, label });
      linked.add(pairKey(parent.key, key));
    }
  }
  return { nodes, edges, added };
}

/** 收起一个节点：从它长出来的节点连同它们再长出来的，全部移除。 */
export function collapse(weave: Weave, key: string): Weave {
  const removed = new Set<string>();
  let changed = true;
  while (changed) {
    changed = false;
    for (const node of weave.nodes) {
      if (node.parent && (node.parent === key || removed.has(node.parent)) && !removed.has(node.key)) {
        removed.add(node.key);
        changed = true;
      }
    }
  }
  return {
    nodes: weave.nodes
      .filter((node) => !removed.has(node.key))
      .map((node) => (node.key === key && node.kind !== "bundle" ? { ...node, expanded: false } : node)),
    edges: weave.edges.filter((edge) => !removed.has(edge.from) && !removed.has(edge.to)),
  };
}

/** 从一个节点沿着长出来的路回到起点，经过的节点。 */
export function pathToRoot(weave: Weave, key: string): string[] {
  const byKey = new Map(weave.nodes.map((node) => [node.key, node]));
  const path: string[] = [];
  let current = byKey.get(key);
  while (current && !path.includes(current.key)) {
    path.push(current.key);
    current = current.parent ? byKey.get(current.parent) : undefined;
  }
  return path;
}

export function nodeX(node: { col: number }): number {
  return node.col * COL_STEP;
}

/** 一组节点占的范围，包含节点自身的宽高。 */
export function bounds(nodes: readonly WeaveNode[]): { x: number; y: number; width: number; height: number } {
  if (!nodes.length) return { x: 0, y: 0, width: 0, height: 0 };
  const xs = nodes.map(nodeX);
  const ys = nodes.map((node) => node.y);
  const left = Math.min(...xs) - NODE_W / 2;
  const top = Math.min(...ys) - NODE_H / 2;
  return {
    x: left,
    y: top,
    width: Math.max(...xs) + NODE_W / 2 - left,
    height: Math.max(...ys) + NODE_H / 2 - top,
  };
}

/**
 * 两个节点之间的连线和标签的位置。不同栏之间从左节点的右边画到右节点的左边；
 * 同一栏的从右边绕出去。标签写在 to 那一端，同一栏里排开的兄弟节点的标签不会叠在一起。
 */
export function edgeGeometry(
  from: WeaveNode,
  to: WeaveNode,
): { d: string; label: { x: number; y: number; anchor: "start" | "end" } } {
  if (from.col === to.col) {
    const x = nodeX(from) + NODE_W / 2;
    const bend = x + 40 + Math.min(60, Math.abs(to.y - from.y) / 4);
    return {
      d: `M${x} ${from.y} C${bend} ${from.y} ${bend} ${to.y} ${x} ${to.y}`,
      label: { x: x + 8, y: to.y - 8, anchor: "start" },
    };
  }
  const [left, right] = from.col < to.col ? [from, to] : [to, from];
  const x1 = nodeX(left) + NODE_W / 2;
  const x2 = nodeX(right) - NODE_W / 2;
  const mid = (x1 + x2) / 2;
  const d = `M${x1} ${left.y} C${mid} ${left.y} ${mid} ${right.y} ${x2} ${right.y}`;
  return to === right
    ? { d, label: { x: x2 - 8, y: right.y - 7, anchor: "end" } }
    : { d, label: { x: x1 + 8, y: left.y - 7, anchor: "start" } };
}

function pairKey(a: string, b: string): string {
  return a < b ? `${a}|${b}` : `${b}|${a}`;
}

/**
 * 新节点在各自的栏里以父节点的高度为中心排开，然后每一栏把重叠的节点上下推开，
 * 已有节点之间的先后不变。
 */
export function spread(nodes: WeaveNode[], added: ReadonlySet<string>): WeaveNode[] {
  const step = NODE_H + GAP_Y;
  const byCol = new Map<number, WeaveNode[]>();
  for (const node of nodes) byCol.set(node.col, [...(byCol.get(node.col) ?? []), node]);
  const placed = new Map<string, number>();
  for (const column of byCol.values()) {
    // 同一个地方长出来的新节点一起排开。
    const fresh = new Map<string, WeaveNode[]>();
    for (const node of column) {
      if (!added.has(node.key)) continue;
      const at = `${node.parent}@${node.y}`;
      fresh.set(at, [...(fresh.get(at) ?? []), node]);
    }
    for (const siblings of fresh.values())
      siblings.forEach((node, i) => placed.set(node.key, node.y + (i - (siblings.length - 1) / 2) * step));
    const items = column.map((node, order) => ({ key: node.key, y: placed.get(node.key) ?? node.y, order }));
    items.sort((a, b) => a.y - b.y || a.order - b.order);
    for (let pass = 0; pass < 200; pass++) {
      let moved = false;
      for (let i = 1; i < items.length; i++) {
        const overlap = step - (items[i]!.y - items[i - 1]!.y);
        if (overlap > 0.5) {
          items[i - 1]!.y -= overlap / 2;
          items[i]!.y += overlap / 2;
          moved = true;
        }
      }
      if (!moved) break;
    }
    for (const item of items) placed.set(item.key, item.y);
  }
  return nodes.map((node) => ({ ...node, y: Math.round(placed.get(node.key) ?? node.y) }));
}
