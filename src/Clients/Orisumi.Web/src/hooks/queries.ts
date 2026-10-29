import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { isUnreadable } from "@/api/client";
import { useSource } from "@/api/source";
import type { ListEntitiesQuery } from "@/api/types";

const retry = (count: number, error: unknown) => !isUnreadable(error) && count < 2;

/** 版本不会改变，读到后一直缓存。 */
export function useVersion(versionId: string | null | undefined) {
  const source = useSource();
  return useQuery({
    queryKey: ["version", versionId],
    queryFn: () => source.getVersion(versionId!),
    enabled: !!versionId,
    staleTime: Infinity,
    retry,
  });
}

export function useEntity(entityId: string) {
  const source = useSource();
  return useQuery({ queryKey: ["entity", entityId], queryFn: () => source.getEntity(entityId), retry });
}

export function useEntityVersions(entityId: string | null | undefined) {
  const source = useSource();
  return useQuery({
    queryKey: ["entity", entityId, "versions"],
    queryFn: () => source.listEntityVersions(entityId!).then((r) => r.versions),
    enabled: !!entityId,
    retry,
  });
}

export function useReferrers(entityId: string | null) {
  const source = useSource();
  return useInfiniteQuery({
    queryKey: ["entity", entityId, "referrers"],
    queryFn: ({ pageParam }) =>
      source.listReferrers(entityId!, { limit: 50, ...(pageParam ? { after: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.next ?? undefined,
    enabled: !!entityId,
    retry,
  });
}

export type EntityFilter = Pick<ListEntitiesQuery, "category" | "resourceName">;

export const COUNT_LIMIT = 100;

/** 一类实体的数量，最多数到 100；更多时 more 为 true。 */
export function useEntityCount(filter: EntityFilter) {
  const source = useSource();
  return useQuery({
    queryKey: ["entities", filter, "count"],
    queryFn: () =>
      source
        .listEntities({ ...filter, limit: COUNT_LIMIT })
        .then((page) => ({ count: page.versions.length, more: page.next !== null })),
    retry,
  });
}
