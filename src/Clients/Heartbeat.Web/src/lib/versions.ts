import type { Category, Version } from "@/api/types";

export const CATEGORY_NAMES: Record<Category, string> = {
  observation: "Observation",
  input_set: "InputSet",
  entity_schema: "EntitySchema",
  business: "业务实体",
};

/** 版本在版本链中的序号，从 1 开始。 */
export function versionNumber(chain: Version[], versionId: string): number | null {
  const index = chain.findIndex((v) => v.versionId === versionId);
  return index < 0 ? null : index + 1;
}

/** “v9 / v12”：所属实体已有更新的版本时写出当前版本号。 */
export function versionLabel(number: number, total: number): string {
  return number < total ? `v${number} / v${total}` : `v${number}`;
}

/** 相对前一版变化的字段名，顺序按当前版本的字段。 */
export function changedFields(previous: Version | undefined, current: Version): string[] {
  if (!previous) return [];
  const before = asRecord(previous.content);
  const after = asRecord(current.content);
  const keys = [...new Set([...Object.keys(after), ...Object.keys(before)])];
  return keys.filter((key) => JSON.stringify(before[key]) !== JSON.stringify(after[key]));
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}
