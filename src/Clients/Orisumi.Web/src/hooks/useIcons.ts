import { useQueries, useQuery } from "@tanstack/react-query";
import { isUnreadable, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import { isBinaryValue, type BinaryValue, type Version } from "@/api/types";
import { LOCAL_APPLICATIONS, PRODUCTS } from "@/lib/applications";
import { useContentUrl } from "./queries";
import { useCatalog } from "./useApplications";

/** 从最近的这么多段引用方里找图标；更早的时段多半在图标采集之前，不再往前翻。 */
const RECENT = 20;

interface FoundIcon {
  /** 带着图标的那一段时段，VersionId 越大越新。 */
  versionId: string;
  icon: BinaryValue;
}

async function latestIcon(source: OrisumiSource, entityId: string): Promise<FoundIcon | null> {
  try {
    const page = await source.listReferrers(entityId, { order: "desc", limit: RECENT });
    for (const { version } of page.referrers) {
      const icon = (version.content as Record<string, unknown> | null)?.icon;
      if (isBinaryValue(icon)) return { versionId: version.versionId, icon };
    }
    return null;
  } catch (error) {
    if (isUnreadable(error)) return null;
    throw error;
  }
}

const iconQuery = (source: OrisumiSource, entityId: string | null) => ({
  queryKey: ["icon", entityId],
  queryFn: () => latestIcon(source, entityId!),
  enabled: !!entityId,
  staleTime: 60_000,
});

/**
 * 本机应用和应用产品自己的图标，见 Web 文档的“角色和记号”。本机应用取引用它的前台时段中最近带图标的那一段；
 * 应用产品取它的本机应用里最新的那个。其他实体和还没有图标的应用返回 undefined，记号用类型图标。
 */
export function useThingImage(version: Version | null | undefined, resourceName: string | null | undefined) {
  const source = useSource();
  const catalog = useCatalog();
  const install = resourceName === LOCAL_APPLICATIONS ? (version?.entityId ?? null) : null;
  const product = resourceName === PRODUCTS && version ? version.entityId : null;
  const installs =
    product && catalog.data
      ? catalog.data.installs
          .filter((i) => catalog.data.productOf(i.version)?.version.entityId === product)
          .map((i) => i.version.entityId)
      : [];
  const own = useQuery(iconQuery(source, install));
  const members = useQueries({ queries: installs.map((id) => iconQuery(source, id)) });
  const newest = members
    .map((member) => member.data)
    .reduce<FoundIcon | null>(
      (best, found) => (found && (!best || found.versionId > best.versionId) ? found : best),
      null,
    );
  const icon = install ? own.data?.icon : newest?.icon;
  return useContentUrl(icon ?? null).data;
}
