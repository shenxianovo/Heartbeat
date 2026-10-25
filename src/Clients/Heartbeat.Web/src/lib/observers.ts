import { isUnreadable, type HeartbeatSource } from "@/api/client";
import { isEntitySchema } from "@/api/types";
import { categoryOf, type CategoryKey } from "./categories";

/** 一个 Observer 版本解析出的身份。读不到的版本单独成为一个 Observer。 */
export interface ObserverIdentity {
  key: string;
  entityId: string | null;
  typeName: string | null;
  resourceName: string | null;
  category: CategoryKey;
}

export const UNREADABLE = "不可读取";

export function unreadableIdentity(versionId: string): ObserverIdentity {
  return { key: `unreadable:${versionId}`, entityId: null, typeName: null, resourceName: null, category: "other" };
}

/** Observer 版本 → 实体和 Schema 版本（OBSERVER-1、SCHEMA-4）。版本不会改变，结果可以永久缓存。 */
export async function resolveObserver(source: HeartbeatSource, versionId: string): Promise<ObserverIdentity> {
  let observer;
  try {
    observer = await source.getVersion(versionId);
  } catch (error) {
    if (isUnreadable(error)) return unreadableIdentity(versionId);
    throw error;
  }
  let typeName: string | null = null;
  let resourceName: string | null = null;
  if (observer.schemaVersionId) {
    try {
      const schema = await source.getVersion(observer.schemaVersionId);
      if (isEntitySchema(schema)) {
        typeName = schema.content.displayName;
        resourceName = schema.content.resourceName;
      }
    } catch (error) {
      if (!isUnreadable(error)) throw error;
    }
  }
  return {
    key: observer.entityId,
    entityId: observer.entityId,
    typeName,
    resourceName,
    category: categoryOf(resourceName),
  };
}

/** 名称取类型名；同名的 Observer 有多个时，加 EntityId 的末 4 位。 */
export function nameObservers(identities: ObserverIdentity[]): Map<string, string> {
  const counts = new Map<string, number>();
  for (const identity of identities) {
    if (identity.entityId) counts.set(baseName(identity), (counts.get(baseName(identity)) ?? 0) + 1);
  }
  const names = new Map<string, string>();
  for (const identity of identities) {
    if (!identity.entityId) {
      names.set(identity.key, UNREADABLE);
      continue;
    }
    const name = baseName(identity);
    names.set(identity.key, (counts.get(name) ?? 0) > 1 ? `${name} ${identity.entityId.slice(-4)}` : name);
  }
  return names;
}

function baseName(identity: ObserverIdentity): string {
  return identity.typeName ?? "Observer";
}
