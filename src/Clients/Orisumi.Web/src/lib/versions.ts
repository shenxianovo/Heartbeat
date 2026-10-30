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

/**
 * 相对前一版改了什么，每处一句：数组字段写加入和移出的元素，取值是标量的写新的值，其余写“改了”。
 * 例如 identifiers 多了一条线索时是“加入 macos.bundle-id com.apple.Preview”。
 * 没有前一版时是“新建”，后面是数组字段里的元素。
 */
export function describeChanges(previous: Version | null, current: Version): string[] {
  const after = asRecord(current.content);
  if (!previous)
    return [
      "新建",
      ...Object.values(after)
        .filter(Array.isArray)
        .flatMap((list) => list.map((item) => `加入 ${plain(item)}`)),
    ];
  const before = asRecord(previous.content);
  const changes = changedFields(previous, current).flatMap((key) => {
    const was = before[key];
    const now = after[key];
    if (Array.isArray(was) && Array.isArray(now)) {
      const has = (list: unknown[], item: unknown) =>
        list.some((other) => JSON.stringify(other) === JSON.stringify(item));
      return [
        ...now.filter((item) => !has(was, item)).map((item) => `加入 ${plain(item)}`),
        ...was.filter((item) => !has(now, item)).map((item) => `移出 ${plain(item)}`),
      ];
    }
    if (now === undefined) return [`去掉 ${key}`];
    if (now === null || typeof now !== "object") return [`${key} 改为 ${plain(now)}`];
    return [`${key} 改了`];
  });
  return changes.length ? changes : ["内容相同"];
}

/** 一个值的简短写法：对象按键名的顺序写出它的标量值，用空格隔开。 */
function plain(value: unknown): string {
  if (value === null) return "null";
  if (typeof value !== "object") return String(value);
  return Object.entries(value)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([, item]) => item)
    .filter((item) => item === null || typeof item !== "object")
    .map(plain)
    .join(" ");
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}
