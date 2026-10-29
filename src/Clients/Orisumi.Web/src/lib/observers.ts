import { categoryOf, type CategoryKey } from "./categories";

/** 一个 Observer 实体，或者一组读不到 Observer 版本的 Observation。 */
export interface ObserverIdentity {
  key: string;
  entityId: string | null;
  typeName: string | null;
  resourceName: string | null;
  category: CategoryKey;
}

export const UNREADABLE = "不可读取";
export const UNREADABLE_KEY = "unreadable";

export const UNREADABLE_IDENTITY: ObserverIdentity = {
  key: UNREADABLE_KEY,
  entityId: null,
  typeName: null,
  resourceName: null,
  category: "other",
};

export function observerIdentity(
  entityId: string,
  typeName: string | null,
  resourceName: string | null,
): ObserverIdentity {
  return { key: entityId, entityId, typeName, resourceName, category: categoryOf(resourceName) };
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
