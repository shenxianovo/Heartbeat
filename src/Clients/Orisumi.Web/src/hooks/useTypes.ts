import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { isUnreadable, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isEntitySchema, isObservation, type EntitySchema, type Observation, type Version } from "@/api/types";
import { outgoingReferences } from "@/lib/references";
import { roleOfType, type Role, type RoleIndex } from "@/lib/roles";
import { DAY } from "@/lib/time";

/** 类型图的一个点：一个业务类型，或 Observation、InputSet。 */
export interface TypeNode {
  key: string;
  name: string;
  role: Role;
  schema: Version<EntitySchema> | null;
}

/** 一种类型的实体引用另一种类型，label 是引用所在的字段。 */
export interface TypeEdge {
  from: string;
  to: string;
  label: string;
}

export interface TypeIndex extends RoleIndex {
  /** 写有事实时间的 Observation 的 Observer 类型。其余 Observer 类型只写事实时间未知的陈述。 */
  timedObserverTypes: ReadonlySet<string>;
  nodes: TypeNode[];
  edges: TypeEdge[];
  byResourceName: ReadonlyMap<string, Version<EntitySchema>>;
}

/** 版本不会改变：经过查询缓存读取，和 useVersion 共用结果。 */
export function readVersion(client: QueryClient, source: OrisumiSource, versionId: string): Promise<Version | null> {
  return client
    .fetchQuery({ queryKey: ["version", versionId], queryFn: () => source.getVersion(versionId), staleTime: Infinity })
    .catch((error: unknown) => {
      if (isUnreadable(error)) return null;
      throw error;
    });
}

/** 同时最多 limit 个请求。 */
export async function pool<T, R>(items: readonly T[], limit: number, run: (item: T) => Promise<R>): Promise<R[]> {
  const results: R[] = new Array(items.length);
  let next = 0;
  async function worker() {
    while (next < items.length) {
      const i = next++;
      results[i] = await run(items[i]!);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

const SAMPLE_RANGE = 30 * DAY;

/**
 * 每种业务类型取一个样本：最近 30 天里一份以它为内容的 Observation，以及它的一个实体。
 * 由此知道哪些类型是观测内容、哪些是 Observer，以及类型之间经由哪个字段引用。
 */
async function buildIndex(client: QueryClient, source: OrisumiSource): Promise<TypeIndex> {
  const schemas = (await readAllPages((after) =>
    source.listEntities({ category: "entity_schema", limit: 1000, ...(after ? { after } : {}) }),
  )) as Version<EntitySchema>[];
  const byResourceName = new Map(schemas.map((s) => [s.content.resourceName, s]));
  const contentTypes = new Set<string>();
  const observerTypes = new Set<string>();
  const timedObserverTypes = new Set<string>();
  const edges = new Map<string, TypeEdge>();
  const edge = (from: string, to: string, label: string) => edges.set(`${from}>${to}>${label}`, { from, to, label });

  async function typeOf(versionId: string): Promise<string | null> {
    const version = await readVersion(client, source, versionId);
    if (!version) return null;
    if (version.category !== "business") return version.category;
    if (!version.schemaVersionId) return null;
    const schema = await readVersion(client, source, version.schemaVersionId);
    return schema && isEntitySchema(schema) ? schema.content.resourceName : null;
  }

  const now = Date.now();
  await pool(schemas, 6, async (schema) => {
    const resourceName = schema.content.resourceName;
    const [observed, sample] = await Promise.all([
      source
        .listObservations({
          from: new Date(now - SAMPLE_RANGE).toISOString(),
          to: new Date(now + DAY).toISOString(),
          resourceName,
          limit: 1,
        })
        .catch(() => ({ versions: [] })),
      source.listEntities({ resourceName, limit: 1 }),
    ]);
    const observation = observed.versions[0];
    if (observation && isObservation(observation)) {
      contentTypes.add(resourceName);
      edge("observation", resourceName, "contentVersionId");
      const content = observation.content as Observation;
      const observerType = await typeOf(content.observerVersionId);
      if (observerType) {
        observerTypes.add(observerType);
        timedObserverTypes.add(observerType);
        edge("observation", observerType, "observerVersionId");
      }
      if (content.inputSetVersionId) edge("observation", "input_set", "inputSetVersionId");
    }
    const entity = sample.versions[0];
    if (!entity) return;
    // 事实时间未知的陈述不在按时间取的样本里，例如应用识别 Observer 和用户本人写的应用产品。
    // 它们的 Observer 从这种类型的一个实体的引用方认出。
    if (!observation) {
      const referrers = await source.listReferrers(entity.entityId, { limit: 20 }).catch(() => ({ referrers: [] }));
      if (
        referrers.referrers.some((r) => r.references.some((location) => location.path === "/content/observerVersionId"))
      ) {
        observerTypes.add(resourceName);
        edge("observation", resourceName, "observerVersionId");
      }
    }
    for (const reference of outgoingReferences(entity, schema.content.fields)) {
      const target = await typeOf(reference.versionId);
      if (target) edge(resourceName, target, reference.path.replace(/\[\d+\]/g, "[]"));
    }
  });

  const index: RoleIndex = { contentTypes, observerTypes };
  const nodes: TypeNode[] = [
    { key: "observation", name: "Observation", role: "experience", schema: null },
    { key: "input_set", name: "InputSet", role: "evidence", schema: null },
    ...schemas.map((schema) => ({
      key: schema.content.resourceName,
      name: schema.content.displayName,
      role: roleOfType(schema.content.resourceName, index),
      schema,
    })),
  ];
  return { contentTypes, observerTypes, timedObserverTypes, nodes, edges: [...edges.values()], byResourceName };
}

export function useTypes() {
  const source = useSource();
  const client = useQueryClient();
  return useQuery({
    queryKey: ["types"],
    queryFn: () => buildIndex(client, source),
    staleTime: 5 * 60_000,
  });
}
