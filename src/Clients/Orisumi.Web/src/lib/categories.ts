// Observer 的类别只用于控制台的布局和配色。键是 Observer 的 Schema resourceName。

export type CategoryKey = "desktop" | "web" | "body" | "inference" | "you" | "other";

export interface CategoryInfo {
  key: CategoryKey;
  label: string;
  color: string;
}

export const CATEGORIES: readonly CategoryInfo[] = [
  { key: "desktop", label: "桌面", color: "#1D9E75" },
  { key: "web", label: "网页", color: "#7F77DD" },
  { key: "body", label: "身体", color: "#D4537E" },
  { key: "inference", label: "推断", color: "#D85A30" },
  { key: "you", label: "你", color: "#888780" },
  { key: "other", label: "其他", color: "#B4B2A9" },
];

/** 接入新的 Observer 时在这里加一行。 */
const BY_RESOURCE_NAME: Record<string, CategoryKey> = {
  "foreground-state-observers": "desktop",
};

export function categoryOf(resourceName: string | null | undefined): CategoryKey {
  return (resourceName && BY_RESOURCE_NAME[resourceName]) || "other";
}

export function categoryInfo(key: CategoryKey): CategoryInfo {
  return CATEGORIES.find((c) => c.key === key) ?? CATEGORIES[CATEGORIES.length - 1]!;
}

/** 图鉴和实体页给每个 Schema 一个颜色：登记过类别的用类别色，其余按 resourceName 从一组柔和的颜色里稳定地取一个。 */
const TINTS = ["#3f9a8a", "#5f86a8", "#c0904a", "#c4707f", "#7f9a52", "#4c93b4", "#c27052", "#8a8f6a"];

export function tintOf(resourceName: string | null | undefined): string {
  const category = categoryOf(resourceName);
  if (category !== "other") return categoryInfo(category).color;
  if (!resourceName) return CATEGORIES[CATEGORIES.length - 1]!.color;
  let hash = 0;
  for (const char of resourceName) hash = (hash * 31 + char.charCodeAt(0)) | 0;
  return TINTS[Math.abs(hash) % TINTS.length]!;
}
