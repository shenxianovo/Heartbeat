import type { FieldDefinition } from "@/api/types";
import { interactionTitle, sessionLabel, type InteractionContent, type SessionContent } from "./conversations";

// 业务实体在界面上的名字。默认取第一个不是引用的文字字段；自己没有这样的字段时，按 Schema 的 resourceName 在这里登记取法。

type Content = Record<string, unknown>;

/** 接入新的 Schema、它又没有文字字段时，在这里加一行。键是 Schema 的 resourceName。 */
const BY_RESOURCE_NAME: Record<string, (content: Content) => string | undefined> = {
  // 本机应用只保存标识线索，名字在引用它的前台时段里。先用自己的 bundle id。
  "local-applications": (content) => identifier(content, "macos.bundle-id"),
  // 助手会话的第一个文字字段是产品，名字写产品和来源标识。
  "assistant-sessions": (content) => sessionLabel(content as unknown as SessionContent),
  // 助手交互的文字在片段里。
  "assistant-interactions": (content) => interactionTitle(content as unknown as InteractionContent),
};

/** 业务内容的名字；返回的 key 是名字所在的字段，摘要里不再重复它。 */
export function businessTitle(
  content: unknown,
  fields: Record<string, FieldDefinition> | undefined,
  resourceName: string | null | undefined,
): { text: string; key?: string } | null {
  const values = (content ?? {}) as Content;
  const registered = resourceName ? BY_RESOURCE_NAME[resourceName]?.(values) : undefined;
  if (registered) return { text: registered };
  const definitions = fields ?? {};
  const names = [...Object.keys(definitions), ...Object.keys(values).filter((name) => !(name in definitions))];
  const key = names.find((name) => typeof values[name] === "string" && definitions[name]?.type !== "reference");
  return key ? { text: values[key] as string, key } : null;
}

function identifier(content: Content, scheme: string): string | undefined {
  const items = Array.isArray(content.identifiers) ? (content.identifiers as unknown[]) : [];
  for (const item of items) {
    const entry = item as { scheme?: unknown; value?: unknown } | null;
    if (entry?.scheme === scheme && typeof entry.value === "string") return entry.value;
  }
  return undefined;
}
