import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import { isEntitySchema, type EntitySchema, type Observation, type Version } from "@/api/types";
import type { Experience, PointGroup } from "@/lib/day";
import { outgoingReferences } from "@/lib/references";
import { roleOf } from "@/lib/roles";
import { dayOf, dayRange, parseTime, SECOND } from "@/lib/time";
import { businessTitle } from "@/lib/titles";
import { useNow } from "./useNow";
import { pool, readVersion, useTypes, type TypeIndex } from "./useTypes";

export const DAY_INTERVAL = 5 * SECOND;

export interface DayData {
  experiences: Experience[];
  points: PointGroup[];
  /** 这一天写过 Observation 的 Observer 版本，按 Observer 实体去重。 */
  observers: Version[];
}

/** 一天的经历：读出这一天的全部 Observation，解析有时段的那些的内容和它们引用的事物。今天的数据每 5 秒刷新。 */
export function useDay(key: string) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  const now = useNow(60_000);
  const range = dayOf(key) ?? dayRange(now);
  const today = range.key === dayRange(now).key;

  const observations = useQuery({
    queryKey: ["observations", "day", range.key],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({ from: range.from, to: range.to, limit: 1000, ...(after ? { after } : {}) }),
      ) as Promise<Version<Observation>[]>,
    refetchInterval: today ? DAY_INTERVAL : false,
  });

  const resolved = useQuery({
    queryKey: ["day", range.key, observations.dataUpdatedAt],
    queryFn: () => resolve(observations.data!, types.data!),
    enabled: !!observations.data && !!types.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  async function resolve(list: Version<Observation>[], index: TypeIndex): Promise<DayData> {
    const read = (id: string | null | undefined) => (id ? readVersion(client, source, id) : Promise.resolve(null));
    const schemaOf = async (version: Version | null) => {
      const schema = await read(version?.category === "business" ? version.schemaVersionId : null);
      return schema && isEntitySchema(schema) ? (schema as Version<EntitySchema>) : null;
    };

    const observerVersions = [...new Set(list.map((o) => o.content.observerVersionId))];
    const observers = (await pool(observerVersions, 8, read)).filter((v): v is Version => !!v);
    const observerEntity = new Map(observers.map((v) => [v.versionId, v]));
    const now = Date.now();

    const periods: Version<Observation>[] = [];
    const pointsByObserver = new Map<string, number[]>();
    for (const observation of list) {
      const start = parseTime(observation.content.startAt);
      const end = parseTime(observation.content.endAt);
      if (start !== null && (end === null || end > start)) periods.push(observation);
      else {
        const at = start ?? end;
        if (at === null) continue;
        const key = observation.content.observerVersionId;
        const times = pointsByObserver.get(key) ?? [];
        times.push(at);
        pointsByObserver.set(key, times);
      }
    }

    const experiences = await pool(periods, 12, async (observation): Promise<Experience> => {
      const content = await read(observation.content.contentVersionId);
      const schema = await schemaOf(content);
      const things: Experience["things"] = [];
      if (content) {
        for (const reference of outgoingReferences(content, schema?.content.fields)) {
          const target = await read(reference.versionId);
          if (!target) continue;
          const targetSchema = await schemaOf(target);
          if (roleOf(target, targetSchema?.content.resourceName, index) !== "thing") continue;
          const title =
            businessTitle(target.content, targetSchema?.content.fields, targetSchema?.content.resourceName)?.text ??
            targetSchema?.content.displayName ??
            "事物";
          things.push({ version: target, title, typeName: targetSchema?.content.displayName ?? "事物" });
        }
      }
      return {
        observation,
        content,
        contentType: schema?.content.resourceName ?? "unknown",
        contentTypeName: schema?.content.displayName ?? "观测内容",
        contentTitle: content
          ? (businessTitle(content.content, schema?.content.fields, schema?.content.resourceName)?.text ?? null)
          : null,
        things,
        observerEntityId: observerEntity.get(observation.content.observerVersionId)?.entityId ?? null,
        start: parseTime(observation.content.startAt)!,
        end: parseTime(observation.content.endAt) ?? now,
      };
    });

    const points: PointGroup[] = [];
    for (const [versionId, times] of pointsByObserver) {
      const observer = observerEntity.get(versionId);
      const schema = await schemaOf(observer ?? null);
      points.push({ key: observer?.entityId ?? versionId, title: schema?.content.displayName ?? "Observer", times });
    }

    const unique = new Map<string, Version>();
    for (const observer of observers) unique.set(observer.entityId, observer);
    return { experiences, points, observers: [...unique.values()] };
  }

  return {
    range,
    today,
    data: resolved.data,
    loading: observations.isPending || resolved.isPending,
    error: observations.error ?? resolved.error ?? types.error,
  };
}
