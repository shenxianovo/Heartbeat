import type { Version } from "@/api/types";

// 实体在记录里扮演的角色，从 Core 推出：被 observerVersionId 引用的是来源，被 contentVersionId 引用的是经历，
// 其余业务实体是事物。角色决定记号的形状；颜色只给事物。

export type Role = "thing" | "experience" | "source" | "evidence" | "definition";

export const ROLE_NAMES: Record<Role, string> = {
  thing: "事物",
  experience: "经历",
  source: "来源",
  evidence: "依据",
  definition: "定义",
};

/** 哪些业务类型是观测内容、哪些是 Observer。键是 resourceName。 */
export interface RoleIndex {
  contentTypes: ReadonlySet<string>;
  observerTypes: ReadonlySet<string>;
}

export function roleOf(version: Version, resourceName: string | null | undefined, index: RoleIndex): Role {
  if (version.category === "observation") return "experience";
  if (version.category === "input_set") return "evidence";
  if (version.category === "entity_schema") return "definition";
  if (resourceName && index.observerTypes.has(resourceName)) return "source";
  if (resourceName && index.contentTypes.has(resourceName)) return "experience";
  return "thing";
}

/** 业务类型的角色；Core 类型用自己的 category 作键。 */
export function roleOfType(key: string, index: RoleIndex): Role {
  if (key === "observation") return "experience";
  if (key === "input_set") return "evidence";
  if (key === "entity_schema") return "definition";
  if (index.observerTypes.has(key)) return "source";
  if (index.contentTypes.has(key)) return "experience";
  return "thing";
}

const THING_COLORS = [
  "#4f8fbf",
  "#d08a45",
  "#4f9f73",
  "#c76b7c",
  "#3a9ea0",
  "#b49a3c",
  "#c0634a",
  "#6a7fb0",
  "#7f9a52",
  "#a88a6a",
];

/** 事物的颜色按名字稳定地取，同名的事物（例如同一个 bundle id 的几个安装）颜色相同。 */
export function thingColor(name: string): string {
  let hash = 0;
  for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) | 0;
  return THING_COLORS[Math.abs(hash) % THING_COLORS.length]!;
}

/**
 * 同一组路径里，各自与众不同的那一段，例如 `…/Release/…` 和 `…/Debug/…`。
 * 路径相同或只有一条时返回 null。
 */
export function distinctSegment(path: string, others: readonly string[]): string | null {
  const parts = path.split("/");
  const rest = others.filter((other) => other !== path).map((other) => other.split("/"));
  if (!rest.length) return null;
  let start = 0;
  while (start < parts.length && rest.every((other) => other[start] === parts[start])) start++;
  let end = 0;
  while (
    end < parts.length - start &&
    rest.every(
      (other) => other.length - 1 - end >= 0 && other[other.length - 1 - end] === parts[parts.length - 1 - end],
    )
  )
    end++;
  const middle = parts.slice(start, parts.length - end);
  if (!middle.length) return null;
  return `${start > 0 ? "…/" : ""}${middle.join("/")}${end > 0 ? "/…" : ""}`;
}

/** 一组内容里第一个取值不同的文字，路径只留不同的那一段。 */
export function differences(contents: unknown[]): (string | null)[] {
  const leaves = contents.map((content) => {
    const out = new Map<string, string>();
    walk(content, "", out);
    return out;
  });
  const paths = [...new Set(leaves.flatMap((map) => [...map.keys()]))];
  const path = paths.find((p) => new Set(leaves.map((map) => map.get(p))).size > 1);
  if (!path) return contents.map(() => null);
  const values = leaves.map((map) => map.get(path) ?? "");
  return values.map((value) => (value.includes("/") ? distinctSegment(value, values) : value || null));
}

function walk(value: unknown, path: string, out: Map<string, string>) {
  if (typeof value === "string") out.set(path, value);
  else if (Array.isArray(value)) value.forEach((item, i) => walk(item, `${path}[${i}]`, out));
  else if (value && typeof value === "object")
    for (const [key, item] of Object.entries(value)) walk(item, path ? `${path}.${key}` : key, out);
}
