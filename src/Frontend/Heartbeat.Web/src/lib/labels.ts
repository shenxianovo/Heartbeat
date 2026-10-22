import type { EntityCategory, EntityResponse } from "@/api/types";

/** 从实体信封里取最可能的人类可读名称；没有则返回 null。 */
export function entityLabel(entity: EntityResponse | undefined, id: string): string | null {
  if (!entity) return null;
  const props = entity.properties;
  const candidates = ["name", "title", "displayName", "resourceName"];
  for (const key of candidates) {
    const v = props[key];
    if (typeof v === "string" && v.trim()) return v;
  }
  if (entity.category === "observation") {
    const start = props.startAt;
    return typeof start === "string" ? `观测 · ${start}` : `观测 · ${shortId(id)}`;
  }
  return null;
}

export function shortId(id: string): string {
  return id.length > 13 ? `${id.slice(0, 8)}…${id.slice(-4)}` : id;
}

export const CATEGORY_STYLE: Record<EntityCategory, { dot: string; text: string; ring: string; label: string }> = {
  observer: {
    dot: "bg-cat-observer",
    text: "text-cat-observer",
    ring: "ring-cat-observer/40",
    label: "观测主体",
  },
  observation: {
    dot: "bg-cat-observation",
    text: "text-cat-observation",
    ring: "ring-cat-observation/40",
    label: "观测",
  },
  entity_schema: {
    dot: "bg-cat-schema",
    text: "text-cat-schema",
    ring: "ring-cat-schema/40",
    label: "实体定义",
  },
  entity: {
    dot: "bg-cat-entity",
    text: "text-cat-entity",
    ring: "ring-cat-entity/40",
    label: "业务实体",
  },
};
