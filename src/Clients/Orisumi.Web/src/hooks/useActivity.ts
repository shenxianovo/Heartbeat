import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isEntitySchema, isObservation, type Observation, type Version } from "@/api/types";
import { observerIdentity, UNREADABLE_IDENTITY, type ObserverIdentity } from "@/lib/observers";
import { dayRange, parseTime, SECOND } from "@/lib/time";
import { useDrafts } from "./queries";
import { useDayObservations } from "./useDay";
import { pool, readVersion, useTypes } from "./useTypes";

export const OBSERVER_INTERVAL = 60 * SECOND;

/** 一种业务类型的全部当前版本；资源名还没有登记时没有这种数据。 */
async function listAll(source: OrisumiSource, resourceName: string): Promise<Version[]> {
  try {
    return await readAllPages((after) =>
      source.listEntities({ resourceName, limit: 1000, ...(after ? { after } : {}) }),
    );
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return [];
    throw error;
  }
}

/** 引用一个 Observer 的全部 Observation 中，保存时间在 [from, to) 内的。 */
async function savedBetween(source: OrisumiSource, entityId: string, from: number, to: number) {
  const found: Version<Observation>[] = [];
  let after: string | undefined;
  do {
    const page = await source.listReferrers(entityId, { limit: 1000, ...(after ? { after } : {}) });
    for (const { version } of page.referrers) {
      const at = parseTime(version.recordedAt);
      if (isObservation(version) && at !== null && at >= from && at < to) found.push(version);
    }
    after = page.next ?? undefined;
  } while (after);
  return found;
}

/**
 * Observer 页面的数据：全部 Observer 实体、事实时间在今天的 Observation、只写事实时间未知的陈述的 Observer
 * 今天保存的 Observation，以及草稿。各行由 observerRows 组合。
 */
export function useActivity(now: number) {
  const source = useSource();
  const client = useQueryClient();
  const types = useTypes();
  const day = dayRange(now);
  const index = types.data;
  const observerTypes = index ? [...index.observerTypes].sort() : [];

  const timed = useDayObservations(day, true);

  const drafts = useDrafts();

  const observers = useQuery({
    queryKey: ["observers", observerTypes],
    queryFn: async () =>
      (
        await Promise.all(
          observerTypes.map(async (resourceName) =>
            (await listAll(source, resourceName)).map((version) => ({ resourceName, version })),
          ),
        )
      ).flat(),
    enabled: !!index,
    refetchInterval: OBSERVER_INTERVAL,
  });

  const timeless = (observers.data ?? [])
    .filter((o) => !index?.timedObserverTypes.has(o.resourceName))
    .map((o) => o.version);
  const recorded = useQuery({
    queryKey: ["observers", "recorded", day.key, timeless.map((o) => o.entityId)],
    queryFn: async () => (await pool(timeless, 4, (o) => savedBetween(source, o.entityId, day.start, day.end))).flat(),
    enabled: !!observers.data,
    refetchInterval: OBSERVER_INTERVAL,
    placeholderData: keepPreviousData,
  });

  // Observation 引用的是 Observer 的某个版本：读出版本所属的实体和它的类型。版本不会改变，结果一直缓存。
  const versionIds = [
    ...new Set([
      ...(observers.data ?? []).map((o) => o.version.versionId),
      ...(timed.data ?? []).map((o) => o.content.observerVersionId),
      ...(recorded.data ?? []).map((o) => o.content.observerVersionId),
      ...(drafts.data ?? []).map((d) => d.content.observerVersionId),
    ]),
  ].sort();
  const identities = useQuery({
    queryKey: ["observers", "identities", versionIds],
    queryFn: async () => {
      const entries = await pool(versionIds, 8, async (versionId) => {
        const version = await readVersion(client, source, versionId);
        if (!version) return [versionId, UNREADABLE_IDENTITY] as const;
        const schema = version.schemaVersionId ? await readVersion(client, source, version.schemaVersionId) : null;
        const content = schema && isEntitySchema(schema) ? schema.content : null;
        return [
          versionId,
          observerIdentity(version.entityId, content?.displayName ?? null, content?.resourceName ?? null),
        ] as const;
      });
      return new Map<string, ObserverIdentity>(entries);
    },
    enabled: !!observers.data && !!timed.data,
    placeholderData: keepPreviousData,
    staleTime: Infinity,
  });

  const identityOf = (versionId: string) => identities.data?.get(versionId);
  return {
    day,
    timedTypes: index?.timedObserverTypes ?? new Set<string>(),
    observers: (observers.data ?? []).flatMap((o) => identityOf(o.version.versionId) ?? []),
    identityOf,
    timed: timed.data ?? [],
    recorded: recorded.data ?? [],
    drafts: drafts.data ?? [],
    loading: types.isPending || timed.isPending || observers.isPending || identities.isPending,
    error: types.error ?? timed.error ?? observers.error ?? recorded.error ?? identities.error,
  };
}
