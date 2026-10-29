import { useMemo } from "react";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { readAllPages } from "@/api/client";
import { useSource } from "@/api/source";
import {
  isEntitySchema,
  type EntitySchema,
  type Observation,
  type ObservationPreview,
  type Version,
} from "@/api/types";
import { withPreviews, type Experience, type PointGroup } from "@/lib/day";
import { outgoingReferences } from "@/lib/references";
import { roleOf } from "@/lib/roles";
import { dayOf, dayRange, parseTime, SECOND } from "@/lib/time";
import { businessTitle } from "@/lib/titles";
import { usePreviews } from "./queries";
import { useNow } from "./useNow";
import { pool, readVersion, useTypes, type TypeIndex } from "./useTypes";

export const DAY_INTERVAL = 5 * SECOND;

export interface DayData {
  experiences: Experience[];
  points: PointGroup[];
  /** 这一天写过 Observation 的 Observer 版本，按 Observer 实体去重。 */
  observers: Version[];
}

/** 事实时间在这一天的全部 Observation。今天的每 5 秒刷新，日页面和 Observer 页面共用。 */
export function useDayObservations(range: ReturnType<typeof dayRange>, today: boolean) {
  const source = useSource();
  return useQuery({
    queryKey: ["observations", "day", range.key],
    queryFn: () =>
      readAllPages((after) =>
        source.listObservations({ from: range.from, to: range.to, limit: 1000, ...(after ? { after } : {}) }),
      ) as Promise<Version<Observation>[]>,
    refetchInterval: today ? DAY_INTERVAL : false,
  });
}

/** 一份要解析的经历：已保存的 Observation，或者预览。 */
interface Pending {
  observationId: string;
  observation: Version<Observation> | null;
  statement: Observation;
  previewAt: number | null;
  writtenAt: number;
}

/**
 * 一天的经历：读出这一天的全部 Observation，解析有时段的那些的内容和它们引用的事物。
 * 今天的数据每 5 秒刷新；今天还读预览，进行中的时段由预览接上。
 */
export function useDay(key: string) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  const now = useNow(60_000);
  const range = dayOf(key) ?? dayRange(now);
  const today = range.key === dayRange(now).key;

  const observations = useDayObservations(range, today);
  const previews = usePreviews(today);

  const resolved = useQuery({
    queryKey: ["day", range.key, observations.dataUpdatedAt],
    queryFn: () => resolve(observations.data!, types.data!),
    enabled: !!observations.data && !!types.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  // 预览只有几份，单独解析，不必每 2 秒重解析整天的 Observation。
  const ongoing = useQuery({
    queryKey: ["day", range.key, "previews", previews.dataUpdatedAt],
    queryFn: () => resolvePreviews(previews.data!, types.data!),
    enabled: today && !!previews.data && !!types.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  const read = (id: string | null | undefined) => (id ? readVersion(client, source, id) : Promise.resolve(null));
  const schemaOf = async (version: Version | null) => {
    const schema = await read(version?.category === "business" ? version.schemaVersionId : null);
    return schema && isEntitySchema(schema) ? (schema as Version<EntitySchema>) : null;
  };

  async function experienceOf(item: Pending, index: TypeIndex, now: number): Promise<Experience> {
    const content = await read(item.statement.contentVersionId);
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
    const observer = await read(item.statement.observerVersionId);
    return {
      observationId: item.observationId,
      observation: item.observation,
      observerVersionId: item.statement.observerVersionId,
      previewAt: item.previewAt,
      writtenAt: item.writtenAt,
      content,
      contentType: schema?.content.resourceName ?? "unknown",
      contentTypeName: schema?.content.displayName ?? "观测内容",
      contentTitle: content
        ? (businessTitle(content.content, schema?.content.fields, schema?.content.resourceName)?.text ?? null)
        : null,
      things,
      observerEntityId: observer?.entityId ?? null,
      start: parseTime(item.statement.startAt)!,
      end: parseTime(item.statement.endAt) ?? now,
    };
  }

  async function resolve(list: Version<Observation>[], index: TypeIndex): Promise<DayData> {
    const observerVersions = [...new Set(list.map((o) => o.content.observerVersionId))];
    const observers = (await pool(observerVersions, 8, read)).filter((v): v is Version => !!v);
    const observerEntity = new Map(observers.map((v) => [v.versionId, v]));
    const now = Date.now();

    const periods: Version<Observation>[] = [];
    const pointsByObserver = new Map<string, number[]>();
    for (const observation of list) {
      if (isPeriod(observation.content)) periods.push(observation);
      else {
        const at = parseTime(observation.content.startAt) ?? parseTime(observation.content.endAt);
        if (at === null) continue;
        const key = observation.content.observerVersionId;
        const times = pointsByObserver.get(key) ?? [];
        times.push(at);
        pointsByObserver.set(key, times);
      }
    }

    const experiences = await pool(periods, 12, (observation) =>
      experienceOf(
        {
          observationId: observation.entityId,
          observation,
          statement: observation.content,
          previewAt: null,
          writtenAt: Math.min(parseTime(observation.recordedAt) ?? now, now),
        },
        index,
        now,
      ),
    );

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

  async function resolvePreviews(list: ObservationPreview[], index: TypeIndex): Promise<Experience[]> {
    const now = Date.now();
    const periods = list.filter((preview) => {
      if (!isPeriod(preview.content)) return false;
      const start = parseTime(preview.content.startAt)!;
      const end = parseTime(preview.content.endAt) ?? now;
      return start < range.end && end > range.start;
    });
    return pool(periods, 4, (preview) => {
      const at = Math.min(parseTime(preview.updatedAt) ?? now, now);
      return experienceOf(
        {
          observationId: preview.entityId,
          observation: null,
          statement: preview.content,
          previewAt: at,
          writtenAt: at,
        },
        index,
        now,
      );
    });
  }

  const data = useMemo(
    () =>
      resolved.data && {
        ...resolved.data,
        experiences: today ? withPreviews(resolved.data.experiences, ongoing.data ?? []) : resolved.data.experiences,
      },
    [resolved.data, ongoing.data, today],
  );

  return {
    range,
    today,
    data,
    loading: observations.isPending || resolved.isPending,
    error: observations.error ?? resolved.error ?? types.error,
  };
}

/** 有时段：开始已知，结束未知或晚于开始。 */
function isPeriod(statement: Observation): boolean {
  const start = parseTime(statement.startAt);
  const end = parseTime(statement.endAt);
  return start !== null && (end === null || end > start);
}
