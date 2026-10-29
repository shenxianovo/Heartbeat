import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, isUnreadable, readAllPages, type OrisumiSource } from "@/api/client";
import { useSource } from "@/api/source";
import type { Version, VersionSubmission } from "@/api/types";
import {
  buildCatalog,
  LOCAL_APPLICATIONS,
  planEdit,
  PRODUCT_SCHEMA,
  PRODUCT_SCHEMA_ID,
  PRODUCTS,
  schemaVersion,
  USER_SCHEMA,
  USER_SCHEMA_ID,
  USERS,
  type Catalog,
  type Edit,
} from "@/lib/applications";
import type { ThingGroup } from "@/lib/day";
import { uuidv7 } from "@/lib/uuid";

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

async function readEntity(source: OrisumiSource, entityId: string): Promise<Version | null> {
  try {
    return await source.getEntity(entityId);
  } catch (error) {
    if (isUnreadable(error)) return null;
    throw error;
  }
}

/** 全部应用产品和本机应用。应用识别 Observer 在后台写入，所以定时重读。 */
export function useCatalog() {
  const source = useSource();
  return useQuery({
    queryKey: CATALOG_KEY,
    queryFn: async (): Promise<Catalog> => {
      const [products, installs] = await Promise.all([listAll(source, PRODUCTS), listAll(source, LOCAL_APPLICATIONS)]);
      return buildCatalog(products, installs);
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

/**
 * 用户本人修改应用产品。一次修改是一次提交：还没保存的 EntitySchema 和用户本人排在前面，
 * 然后是 InputSet、产品的版本和 Observation；合并时最后删除被并入的产品。
 */
export function useApplicationEdit() {
  const source = useSource();
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (edit: Edit) => {
      const catalog = client.getQueryData<Catalog>(CATALOG_KEY);
      if (!catalog) throw new Error("应用目录还没有读出来");
      if (edit.kind === "delete") {
        const plan = planEdit(edit, catalog, { observerVersionId: "", productSchemaVersionId: "", newId: uuidv7 });
        for (const entityId of plan.deletes) await source.deleteEntity(entityId);
        return { summary: plan.summary, registered: false, deleted: plan.deletes };
      }
      const prelude: VersionSubmission[] = [];

      let productSchema = await readEntity(source, PRODUCT_SCHEMA_ID);
      if (!productSchema) {
        const first = schemaVersion(PRODUCT_SCHEMA_ID, uuidv7(), PRODUCT_SCHEMA);
        prelude.push(first);
        productSchema = { ...first, recordedAt: "" };
      }
      let observer = (await listAll(source, USERS)).sort((a, b) => a.entityId.localeCompare(b.entityId))[0];
      if (!observer) {
        let userSchema = await readEntity(source, USER_SCHEMA_ID);
        if (!userSchema) {
          const first = schemaVersion(USER_SCHEMA_ID, uuidv7(), USER_SCHEMA);
          prelude.push(first);
          userSchema = { ...first, recordedAt: "" };
        }
        const user: VersionSubmission = {
          entityId: uuidv7(),
          versionId: uuidv7(),
          previousVersionId: null,
          category: "business",
          schemaVersionId: userSchema.versionId,
          content: { name: null },
        };
        prelude.push(user);
        observer = { ...user, recordedAt: "" };
      }

      const plan = planEdit(edit, catalog, {
        observerVersionId: observer.versionId,
        productSchemaVersionId: productSchema.versionId,
        newId: () => uuidv7(),
      });
      await source.submitVersions([...prelude, ...plan.versions]);
      for (const entityId of plan.deletes) await source.deleteEntity(entityId);
      return { summary: plan.summary, registered: prelude.length > 0, deleted: plan.deletes };
    },
    onSuccess: ({ registered }) => {
      // 新登记的类型要重新推出角色。
      if (registered) void client.invalidateQueries({ queryKey: ["types"] });
    },
    onSettled: () => {
      void client.invalidateQueries({ queryKey: CATALOG_KEY });
      void client.invalidateQueries({ queryKey: ["entity"] });
    },
  });
}

/** 修改失败时给用户看的话。产品在这期间被另一方改过（VERSION-6）时，提示重新修改。 */
export function editError(error: unknown): string {
  if (error instanceof ApiError && error.problem?.rule === "VERSION-6")
    return "产品在这期间被修改过，已重新读取，请再改一次";
  return error instanceof Error ? error.message : "修改没有保存";
}
