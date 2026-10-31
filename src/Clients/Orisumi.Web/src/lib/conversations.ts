import type { BinaryValue, Observation, Version } from "@/api/types";
import { parseTime } from "./time";

// 助手会话页的纯函数：把一天的交互按会话分组，把工具调用和结果配成一项，按视图筛选。
// 字段含义见文档 Observers · Assistant Sessions。

export type InteractionKind = "message" | "tool-call" | "tool-result" | "reasoning" | "context";
export type Actor = "user" | "assistant" | "tool" | "application" | "agent";

export interface InteractionPart {
  type: "text" | "summary" | "json" | "image" | "opaque";
  text: string | null;
  entryIndex: number;
  pointer: string;
  mediaType: string | null;
  file: BinaryValue | null;
}

export interface InteractionContent {
  sessionVersionId: string;
  client: string | null;
  kind: InteractionKind;
  actor: Actor | null;
  sourceRole: string | null;
  sourceKind: string;
  sourceId: string;
  sourceTurnId: string | null;
  callId: string | null;
  toolCallVersionId: string | null;
  toolName: string | null;
  toolServer: string | null;
  isError: boolean | null;
  exitCode: number | null;
  phase: string | null;
  model: string | null;
  workingDirectory: string | null;
  parts: InteractionPart[];
  sourceEntries: BinaryValue[];
}

export interface SessionContent {
  product: string;
  sourceSessionId: string;
}

/** 写在别的实体上的会话信息：最后写入的标题，以及子 Agent 关系。 */
export interface SessionMeta {
  title: string | null;
  parentEntityId: string | null;
  agentName: string | null;
}

/** 一次交互和它的事实时间。 */
export interface Interaction {
  version: Version<InteractionContent>;
  observationId: string;
  start: number | null;
  end: number | null;
  /** 排序用的时刻：开始已知取开始，否则取结束。 */
  at: number;
}

/** 会话页上的一项：一次交互，或者配好对的工具调用和结果。 */
export type Item =
  | { type: "interaction"; interaction: Interaction }
  | { type: "tool"; call: Interaction | null; result: Interaction | null; at: number };

export interface SessionGroup {
  entityId: string;
  /** 这一天读到的最新的会话版本。 */
  session: Version<SessionContent> | null;
  title: string | null;
  parentEntityId: string | null;
  agentName: string | null;
  client: string | null;
  interactions: Interaction[];
  first: number;
  last: number;
  userMessages: number;
  toolCalls: number;
  workingDirectory: string | null;
}

export type View = "dialog" | "process" | "all";

export const VIEWS: readonly { value: View; label: string }[] = [
  { value: "dialog", label: "对话" },
  { value: "process", label: "过程" },
  { value: "all", label: "全部" },
];

const PRODUCTS: Record<string, string> = { "claude-code": "Claude Code", codex: "Codex" };

export function productName(product: string | null | undefined): string {
  return (product && PRODUCTS[product]) || product || "助手";
}

export function sessionTitle(group: SessionGroup): string {
  if (!group.session) return "读不到的会话";
  if (group.title) return group.title;
  return group.parentEntityId ? `子 Agent · ${group.agentName ?? group.session.content.sourceSessionId}` : "未命名会话";
}

/** 会话实体自己的名字：内容只有产品和来源标识，标题在标题实体上。 */
export function sessionLabel(session: SessionContent): string {
  return `${productName(session.product)} · ${session.sourceSessionId.slice(0, 8)}`;
}

/** 交互的名字：消息取第一段文字，工具取工具名或记录种类。 */
export function interactionTitle(content: InteractionContent): string {
  if (content.kind === "tool-call" || content.kind === "tool-result") return toolLabel(content);
  const text = content.parts.find((part) => part.text)?.text?.trim();
  if (text) return text.length > 80 ? `${text.slice(0, 80)}…` : text;
  return content.kind === "reasoning" ? "推理" : content.kind === "context" ? contextLabel(content) : "消息";
}

export function toolLabel(content: InteractionContent): string {
  return content.toolName ?? content.sourceKind;
}

/** 上下文的种类，例如 Claude Code 的 `attachment/instructions` 写成“instructions”；来源写了角色时带上角色，例如“response_item · developer”。 */
export function contextLabel(content: InteractionContent): string {
  const kind = content.sourceKind.replace(/^attachment\//, "");
  return content.sourceRole ? `${kind} · ${content.sourceRole}` : kind;
}

export function toInteraction(observation: Version<Observation>, content: Version<InteractionContent>): Interaction {
  const start = parseTime(observation.content.startAt);
  const end = parseTime(observation.content.endAt);
  return { version: content, observationId: observation.entityId, start, end, at: start ?? end ?? 0 };
}

/**
 * 按会话分组，组内按时间排列。sessions 是会话版本按 VersionId 的索引，metas 是会话信息按 EntityId 的索引；
 * 同一会话这一天引用过几个版本时，取 VersionId 最大的，也就是最新的那一版。
 */
export function groupSessions(
  interactions: readonly Interaction[],
  sessions: ReadonlyMap<string, Version<SessionContent> | null>,
  metas: ReadonlyMap<string, SessionMeta>,
): SessionGroup[] {
  const groups = new Map<string, SessionGroup>();
  for (const interaction of interactions) {
    const versionId = interaction.version.content.sessionVersionId;
    const session = sessions.get(versionId) ?? null;
    const entityId = session?.entityId ?? versionId;
    let group = groups.get(entityId);
    if (!group) {
      group = {
        entityId,
        session,
        title: metas.get(entityId)?.title ?? null,
        parentEntityId: metas.get(entityId)?.parentEntityId ?? null,
        agentName: metas.get(entityId)?.agentName ?? null,
        client: null,
        interactions: [],
        first: Infinity,
        last: -Infinity,
        userMessages: 0,
        toolCalls: 0,
        workingDirectory: null,
      };
      groups.set(entityId, group);
    }
    if (session && (!group.session || session.versionId > group.session.versionId)) group.session = session;
    group.interactions.push(interaction);
    group.first = Math.min(group.first, interaction.at);
    group.last = Math.max(group.last, interaction.end ?? interaction.at);
    const content = interaction.version.content;
    if (content.kind === "message" && content.actor === "user") group.userMessages++;
    if (content.kind === "tool-call") group.toolCalls++;
  }
  for (const group of groups.values()) {
    group.interactions.sort(byTime);
    const latest = [...group.interactions].reverse();
    group.workingDirectory =
      latest.find((i) => i.version.content.workingDirectory)?.version.content.workingDirectory ?? null;
    group.client = latest.find((i) => i.version.content.client)?.version.content.client ?? null;
  }
  return [...groups.values()].sort((a, b) => b.last - a.last);
}

/** 子 Agent 的会话排在父会话后面，父会话不在这一天时单独排。返回每个会话和它的缩进层级。 */
export function nestSessions(groups: readonly SessionGroup[]): { group: SessionGroup; depth: number }[] {
  const present = new Set(groups.map((g) => g.entityId));
  const children = new Map<string, SessionGroup[]>();
  const roots: SessionGroup[] = [];
  for (const group of groups) {
    if (group.parentEntityId && present.has(group.parentEntityId)) {
      const list = children.get(group.parentEntityId) ?? [];
      list.push(group);
      children.set(group.parentEntityId, list);
    } else roots.push(group);
  }
  const result: { group: SessionGroup; depth: number }[] = [];
  const visit = (group: SessionGroup, depth: number) => {
    result.push({ group, depth });
    for (const child of (children.get(group.entityId) ?? []).sort((a, b) => a.first - b.first)) visit(child, depth + 1);
  };
  for (const root of roots) visit(root, 0);
  return result;
}

/** 按视图筛选，再把工具调用和结果配成一项：结果按 toolCallVersionId 找调用，找不到时按 callId。 */
export function buildItems(interactions: readonly Interaction[], view: View): Item[] {
  const shown = interactions.filter((interaction) => visible(interaction.version.content.kind, view));
  const calls = new Map<string, { type: "tool"; call: Interaction; result: Interaction | null; at: number }>();
  const byCallId = new Map<string, string>();
  const items: Item[] = [];
  for (const interaction of shown) {
    const content = interaction.version.content;
    if (content.kind === "tool-call") {
      const item = { type: "tool" as const, call: interaction, result: null, at: interaction.at };
      calls.set(interaction.version.versionId, item);
      if (content.callId) byCallId.set(content.callId, interaction.version.versionId);
      items.push(item);
    } else if (content.kind === "tool-result") {
      const callVersion =
        content.toolCallVersionId ?? (content.callId ? byCallId.get(content.callId) : undefined) ?? null;
      const call = callVersion ? calls.get(callVersion) : undefined;
      if (call && !call.result) call.result = interaction;
      else items.push({ type: "tool", call: null, result: interaction, at: interaction.at });
    } else items.push({ type: "interaction", interaction });
  }
  return items;
}

function visible(kind: InteractionKind, view: View): boolean {
  if (view === "all") return true;
  if (view === "process") return kind !== "context";
  return kind === "message";
}

function byTime(a: Interaction, b: Interaction): number {
  return a.at - b.at || (a.version.versionId < b.version.versionId ? -1 : 1);
}

/** 按 RFC 6901 的 JSON Pointer 取值，取不到时返回 undefined。 */
export function resolvePointer(document: unknown, pointer: string): unknown {
  if (pointer === "") return document;
  if (!pointer.startsWith("/")) return undefined;
  let current: unknown = document;
  for (const raw of pointer.slice(1).split("/")) {
    const key = raw.replace(/~1/g, "/").replace(/~0/g, "~");
    if (Array.isArray(current)) current = current[Number(key)];
    else if (current && typeof current === "object") current = (current as Record<string, unknown>)[key];
    else return undefined;
  }
  return current;
}

/** 片段的值写成文字：字符串原样，其他按 JSON 缩进。 */
export function partText(value: unknown): string {
  if (value === undefined) return "";
  if (typeof value === "string") return value;
  // 工具结果常是 [{type: "text", text}] 这样的内容块，或者 {content: [...]}；全是文字块时连成正文。
  const blocks = Array.isArray(value)
    ? value
    : value && typeof value === "object" && Array.isArray((value as { content?: unknown }).content)
      ? ((value as { content: unknown[] }).content as unknown[])
      : null;
  if (blocks?.length && blocks.every(isTextBlock)) return blocks.map((block) => block.text).join("\n");
  return JSON.stringify(value, null, 2);
}

/** 文字片段按原样显示；JSON 片段能解析时缩进显示。 */
export function inlineText(part: InteractionPart): string {
  if (part.type !== "json" || part.text === null) return part.text ?? "";
  try {
    return JSON.stringify(JSON.parse(part.text), null, 2);
  } catch {
    return part.text;
  }
}

function isTextBlock(value: unknown): value is { type: "text"; text: string } {
  const block = value as { type?: unknown; text?: unknown } | null;
  return !!block && block.type === "text" && typeof block.text === "string";
}

/** 工具调用的一句摘要：参数是 JSON 时取命令、路径这类常见的字段，否则取开头的一段。 */
export function argumentSummary(content: InteractionContent | undefined): string | null {
  const text = content?.parts.find((part) => part.type === "json" && part.text)?.text;
  if (!text) return null;
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return clip(text);
  }
  if (Array.isArray(value)) return clip(value.map(String).join(" "));
  if (value && typeof value === "object") {
    const record = value as Record<string, unknown>;
    for (const key of ["command", "cmd", "file_path", "path", "url", "query", "pattern", "description"]) {
      const field = record[key];
      if (typeof field === "string") return clip(field);
      if (Array.isArray(field)) return clip(field.map(String).join(" "));
    }
  }
  return clip(text);
}

function clip(text: string): string {
  const line = text.replace(/\s+/g, " ").trim();
  return line.length > 120 ? `${line.slice(0, 120)}…` : line;
}

/** 工作目录的最后一段。 */
export function folderName(path: string | null | undefined): string | null {
  if (!path) return null;
  const parts = path
    .replace(/^file:\/\//, "")
    .split("/")
    .filter(Boolean);
  return parts.at(-1) ?? path;
}
