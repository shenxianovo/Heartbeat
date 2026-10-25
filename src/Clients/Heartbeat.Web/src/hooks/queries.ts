import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { isUnreadable } from "@/api/client";
import { useSource } from "@/api/source";

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

export function useReferrers(entityId: string) {
  const source = useSource();
  return useInfiniteQuery({
    queryKey: ["entity", entityId, "referrers"],
    queryFn: ({ pageParam }) =>
      source.listReferrers(entityId, { limit: 50, ...(pageParam ? { after: pageParam } : {}) }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.next ?? undefined,
    retry,
  });
}
