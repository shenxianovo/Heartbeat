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

/** 演示数据源用到的 resourceName 也在这里登记，和真实 Observer 走同一套映射。 */
export function registerDemoCategories(entries: Record<string, CategoryKey>): void {
  for (const [name, key] of Object.entries(entries)) {
    if (!(name in BY_RESOURCE_NAME)) BY_RESOURCE_NAME[name] = key;
  }
}
