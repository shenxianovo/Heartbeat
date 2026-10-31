import { useQuery } from "@tanstack/react-query";
import { ApiError, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { Version } from "@/api/types";
import { buildCatalog, LOCAL_APPLICATIONS, MEMBERSHIPS, PRODUCTS, type Catalog } from "@/lib/applications";
import type { ThingGroup } from "@/lib/day";

export const CATALOG_KEY = ["applications"] as const;

/** 一种业务类型的全部当前版本。资源名还没有登记时，后端返回 404，这时就是还没有这种数据。 */
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

/** 线索归属引用的产品版本属于哪个实体；已删除的版本不在结果中。 */
async function readTargets(source: OrisumiSource, products: readonly Version[], memberships: readonly Version[]) {
  const targets = new Map(products.map((product) => [product.versionId, product.entityId]));
  const ids = new Set(
    memberships.map((membership) => (membership.content as { productVersionId: string }).productVersionId),
  );
  const missing = [...ids].filter((id) => !targets.has(id));
  for (let i = 0; i < missing.length; i += 256)
    for (const result of (await source.readVersions(missing.slice(i, i + 256))).results)
      if (result.version) targets.set(result.versionId, result.version.entityId);
  return targets;
}

/** 全部应用产品、线索归属和本机应用。应用识别 Observer 在后台写入，所以定时重读。 */
export function useCatalog() {
  const source = useSource();
  return useQuery({
    queryKey: CATALOG_KEY,
    queryFn: async (): Promise<Catalog> => {
      const [products, memberships, installs] = await Promise.all([
        listAll(source, PRODUCTS),
        listAll(source, MEMBERSHIPS),
        listAll(source, LOCAL_APPLICATIONS),
      ]);
      return buildCatalog(products, memberships, installs, await readTargets(source, products, memberships));
    },
    staleTime: 10_000,
    refetchInterval: 30_000,
  });
}

/** 按产品合并：本机应用换成它所属的产品；未归属的本机应用保留，两者都算作“应用”。 */
export function productGroup(catalog: Catalog): ThingGroup {
  const installs = new Set(catalog.installs.map((install) => install.version.entityId));
  return (thing) => {
    if (!installs.has(thing.version.entityId)) return null;
    const product = catalog.productOf(thing.version);
    return product
      ? { version: product.version, title: product.name, typeName: "应用" }
      : { ...thing, typeName: "应用" };
  };
}
