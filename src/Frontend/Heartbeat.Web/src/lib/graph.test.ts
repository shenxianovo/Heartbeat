import { describe, expect, it } from "vitest";
import { buildGraph, layoutRadial } from "./graph";
import type { EntityResponse } from "@/api/types";

const root = "0192b056-7300-7000-8000-000000000001";
const observerId = "0192b056-7300-7000-8000-000000000002";
const contentId = "0192b056-7300-7000-8000-000000000003";
const schemaId = "0192b056-7300-7000-8000-000000000004";

function entity(category: EntityResponse["category"], references: Record<string, string | null>): EntityResponse {
  return { category, references, properties: {} };
}

describe("buildGraph", () => {
  it("从根沿 references 展平节点与边，跳过 null 引用", () => {
    const entities = new Map<string, EntityResponse>([
      [root, entity("observation", { observerId, contentId, schemaId })],
      [observerId, entity("observer", {})],
      [contentId, entity("entity", { localApplicationVersionId: null })],
    ]);
    const { nodes, edges } = buildGraph(root, entities, new Set());

    expect(nodes.map((n) => n.id).sort()).toEqual([root, observerId, contentId, schemaId].sort());
    expect(edges).toEqual([
      { from: root, to: observerId, label: "observerId" },
      { from: root, to: contentId, label: "contentId" },
      { from: root, to: schemaId, label: "schemaId" },
    ]);
  });

  it("未加载的引用目标标记为 loading，404 目标标记 notFound", () => {
    const entities = new Map<string, EntityResponse>([
      [root, entity("observation", { observerId, contentId, schemaId })],
    ]);
    const missing = new Set([observerId]);
    const { nodes } = buildGraph(root, entities, missing);
    const byId = new Map(nodes.map((n) => [n.id, n]));
    expect(byId.get(observerId)?.notFound).toBe(true);
    expect(byId.get(contentId)?.loading).toBe(true);
  });
});

describe("layoutRadial", () => {
  it("根节点放在画布中心，其他节点分到外圈", () => {
    const entities = new Map<string, EntityResponse>([
      [root, entity("observation", { observerId, contentId, schemaId })],
      [observerId, entity("observer", {})],
      [contentId, entity("entity", {})],
      [schemaId, entity("entity_schema", {})],
    ]);
    const { nodes, edges } = buildGraph(root, entities, new Set());
    const pos = layoutRadial(nodes, edges, 800, 600);
    expect(pos.get(root)).toEqual({ x: 400, y: 300 });
    for (const leaf of [observerId, contentId, schemaId]) {
      const p = pos.get(leaf)!;
      expect(Math.hypot(p.x - 400, p.y - 300)).toBeGreaterThan(50);
    }
  });
});
