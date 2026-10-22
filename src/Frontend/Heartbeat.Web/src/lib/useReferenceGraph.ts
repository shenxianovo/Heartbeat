import { useCallback, useMemo, useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { api } from "@/api/client";
import { entityQueryKey } from "@/api/queries";
import type { EntityResponse } from "@/api/types";
import { buildGraph } from "./graph";

/**
 * 引用图状态：从根实体出发，沿 references 自动展开到 autoDepth 层，并支持手动继续展开。
 * 后端暂无批量/图查询接口，每个 ID 单独 GET（TanStack Query 负责缓存去重）。
 * 根切换与自动展开用「渲染期按条件修正 state」的 React 官方模式，避免 effect 级联渲染。
 */
export function useReferenceGraph(rootId: string | null, autoDepth = 2) {
  const [ids, setIds] = useState<string[]>(rootId ? [rootId] : []);
  const [trackedRoot, setTrackedRoot] = useState<string | null>(rootId);

  // 根实体变化时重置已展开集合（渲染期修正，提交前重渲染，不产生额外 effect）。
  if (rootId !== trackedRoot) {
    setTrackedRoot(rootId);
    setIds(rootId ? [rootId] : []);
  }

  const expand = useCallback((newIds: string[]) => {
    setIds((prev) => {
      const additions = newIds.filter((id) => !prev.includes(id));
      return additions.length > 0 ? [...prev, ...additions] : prev;
    });
  }, []);

  const reset = useCallback((id: string) => setIds([id]), []);

  const results = useQueries({
    queries: ids.map((id) => ({
      queryKey: entityQueryKey(id),
      queryFn: () => api.getEntity(id),
      staleTime: 30_000,
      retry: false,
    })),
  });

  const entities = useMemo(() => {
    const map = new Map<string, EntityResponse>();
    results.forEach((r, i) => {
      const id = ids[i];
      if (id && r.data) map.set(id, r.data);
    });
    return map;
  }, [results, ids]);

  const missing = useMemo(() => {
    const set = new Set<string>();
    results.forEach((r, i) => {
      const id = ids[i];
      if (id && r.isError && (r.error as { status?: number })?.status === 404) set.add(id);
    });
    return set;
  }, [results, ids]);

  const graph = useMemo(
    () => (rootId ? buildGraph(rootId, entities, missing) : { nodes: [], edges: [] }),
    [rootId, entities, missing],
  );

  // 浅层自动展开目标：沿已加载实体的引用走到 autoDepth 层。
  const autoTargets = useMemo(() => {
    const wanted = new Set<string>();
    const walked = new Set<string>();
    const walk = (id: string, depth: number) => {
      if (depth > autoDepth) return;
      const ent = entities.get(id);
      if (!ent) return;
      for (const target of Object.values(ent.references)) {
        if (!target) continue;
        if (!entities.has(target) && !missing.has(target)) wanted.add(target);
        if (entities.has(target)) {
          const edgeKey = `${id}->${target}`;
          if (!walked.has(edgeKey)) {
            walked.add(edgeKey);
            walk(target, depth + 1);
          }
        }
      }
    };
    if (rootId && entities.has(rootId)) walk(rootId, 1);
    return wanted;
  }, [entities, missing, rootId, autoDepth]);

  // 已加载数据产生新的浅层目标时，渲染期并入查询集合，React 会在提交前重渲染。
  const pending = [...autoTargets].filter((id) => !ids.includes(id));
  if (pending.length > 0) setIds([...ids, ...pending]);

  // 图中可见但尚未加载（超出自动深度、需点击展开）的引用目标。
  const visibleUnloaded = useMemo(() => {
    const set = new Set<string>();
    for (const node of graph.nodes) {
      if (!entities.has(node.id) && !missing.has(node.id)) set.add(node.id);
    }
    return [...set];
  }, [graph.nodes, entities, missing]);

  return { graph, entities, missing, expandableIds: visibleUnloaded, expand, reset, ids };
}
