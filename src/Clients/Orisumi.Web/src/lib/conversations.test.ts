import { describe, expect, it } from "vitest";
import type { Observation, Version } from "@/api/types";
import {
  argumentSummary,
  buildItems,
  groupSessions,
  nestSessions,
  partText,
  resolvePointer,
  toInteraction,
  type Interaction,
  type InteractionContent,
  type SessionContent,
} from "./conversations";

let counter = 0;
const id = () => `0192b056-7300-7000-8000-${(++counter).toString(16).padStart(12, "0")}`;

function session(content: Partial<SessionContent> = {}): Version<SessionContent> {
  return {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "business",
    schemaVersionId: null,
    recordedAt: "2026-10-31T00:00:00Z",
    content: {
      product: "claude-code",
      sourceSessionId: "s",
      title: null,
      client: null,
      parentSessionVersionId: null,
      agentName: null,
      spawnCallId: null,
      ...content,
    },
  };
}

function interaction(
  sessionVersion: Version<SessionContent>,
  at: string,
  content: Partial<InteractionContent>,
  startAt: string | null = at,
): Interaction {
  const version: Version<InteractionContent> = {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "business",
    schemaVersionId: null,
    recordedAt: at,
    content: {
      sessionVersionId: sessionVersion.versionId,
      kind: "message",
      actor: "user",
      sourceRole: null,
      sourceKind: "user",
      sourceId: id(),
      sourceTurnId: null,
      callId: null,
      toolCallVersionId: null,
      toolName: null,
      toolServer: null,
      isError: null,
      exitCode: null,
      phase: null,
      model: null,
      workingDirectory: null,
      parts: [],
      sourceEntries: [],
      ...content,
    },
  };
  const observation: Version<Observation> = {
    entityId: id(),
    versionId: id(),
    previousVersionId: null,
    category: "observation",
    schemaVersionId: null,
    recordedAt: at,
    content: {
      observerVersionId: id(),
      contentVersionId: version.versionId,
      inputSetVersionId: null,
      startAt,
      endAt: at,
      timeZone: null,
    },
  };
  return toInteraction(observation, version);
}

describe("groupSessions", () => {
  it("按会话分组，组内按时间排列，最近活动的会话在前", () => {
    const a = session({ title: "A" });
    const b = session({ title: "B" });
    const index = new Map([
      [a.versionId, a],
      [b.versionId, b],
    ]);
    const groups = groupSessions(
      [
        interaction(a, "2026-10-31T02:00:00Z", { workingDirectory: "/x/heartbeat" }),
        interaction(b, "2026-10-31T03:00:00Z", {}),
        interaction(a, "2026-10-31T01:00:00Z", { kind: "tool-call", actor: "assistant" }),
      ],
      index,
    );
    expect(groups.map((g) => g.session?.content.title)).toEqual(["B", "A"]);
    expect(groups[1]!.interactions.map((i) => i.version.content.kind)).toEqual(["tool-call", "message"]);
    expect(groups[1]).toMatchObject({ userMessages: 1, toolCalls: 1, workingDirectory: "/x/heartbeat" });
  });

  it("Claude Code 的助手回复只有结束边界，按结束时刻排序", () => {
    const a = session();
    const groups = groupSessions(
      [
        interaction(a, "2026-10-31T01:00:05Z", { actor: "assistant" }, null),
        interaction(a, "2026-10-31T01:00:00Z", { actor: "user" }),
      ],
      new Map([[a.versionId, a]]),
    );
    expect(groups[0]!.interactions.map((i) => i.version.content.actor)).toEqual(["user", "assistant"]);
  });

  it("子 Agent 的会话排在父会话后面", () => {
    const parent = session({ title: "父" });
    const child = session({ parentSessionVersionId: parent.versionId, agentName: "web-fetch" });
    const other = session({ title: "其他" });
    const index = new Map([parent, child, other].map((s) => [s.versionId, s]));
    const groups = groupSessions(
      [
        interaction(parent, "2026-10-31T01:00:00Z", {}),
        interaction(other, "2026-10-31T02:00:00Z", {}),
        interaction(child, "2026-10-31T03:00:00Z", { actor: "agent" }),
      ],
      index,
    );
    expect(nestSessions(groups).map(({ group, depth }) => [group.session?.content.title ?? "子", depth])).toEqual([
      ["其他", 0],
      ["父", 0],
      ["子", 1],
    ]);
  });
});

describe("buildItems", () => {
  const s = session();
  const call = interaction(s, "2026-10-31T01:00:00Z", { kind: "tool-call", actor: "assistant", callId: "c1" });
  const other = interaction(s, "2026-10-31T01:00:01Z", { kind: "tool-call", actor: "assistant", callId: "c2" });
  const result = interaction(s, "2026-10-31T01:00:02Z", {
    kind: "tool-result",
    actor: "tool",
    callId: "c1",
    toolCallVersionId: call.version.versionId,
  });
  const late = interaction(s, "2026-10-31T01:00:03Z", { kind: "tool-result", actor: "tool", callId: "c2" });
  const orphan = interaction(s, "2026-10-31T01:00:04Z", { kind: "tool-result", actor: "tool", callId: "c9" });
  const context = interaction(s, "2026-10-31T00:59:00Z", { kind: "context", actor: "application" });
  const message = interaction(s, "2026-10-31T00:59:30Z", {});

  it("并行的调用按标识配对结果，没有调用的结果单独成一项", () => {
    const items = buildItems([message, call, other, result, late, orphan], "process");
    expect(items).toHaveLength(4);
    expect(items[1]).toMatchObject({ type: "tool", call, result });
    expect(items[2]).toMatchObject({ type: "tool", call: other, result: late });
    expect(items[3]).toMatchObject({ type: "tool", call: null, result: orphan });
  });

  it("对话只留消息，全部才有上下文", () => {
    expect(buildItems([context, message, call, result], "dialog")).toHaveLength(1);
    expect(buildItems([context, message, call, result], "process")).toHaveLength(2);
    expect(buildItems([context, message, call, result], "all")).toHaveLength(3);
  });
});

describe("resolvePointer", () => {
  const line = { message: { content: [{ type: "image", source: { data: "AAAA" } }, { "a/b": { "c~d": 1 } }] } };
  it("按 RFC 6901 解析", () => {
    expect(resolvePointer(line, "/message/content/0/source/data")).toBe("AAAA");
    expect(resolvePointer(line, "/message/content/1/a~1b/c~0d")).toBe(1);
    expect(resolvePointer(line, "/message/missing/0")).toBeUndefined();
    expect(resolvePointer(line, "")).toBe(line);
  });
});

describe("argumentSummary", () => {
  const withArguments = (text: string) =>
    ({ parts: [{ type: "json", text, entryIndex: 0, pointer: "", mediaType: null, file: null }] }) as never;
  it("取命令或路径", () => {
    expect(argumentSummary(withArguments('{"command":"pnpm test","description":"跑测试"}'))).toBe("pnpm test");
    expect(argumentSummary(withArguments('["/bin/zsh","-lc","pwd"]'))).toBe("/bin/zsh -lc pwd");
    expect(argumentSummary(withArguments('{"file_path":"/a/b.ts"}'))).toBe("/a/b.ts");
  });
});

describe("partText", () => {
  it("文字块连成正文，其余按 JSON 缩进", () => {
    expect(partText([{ type: "text", text: "Plan shown" }])).toBe("Plan shown");
    expect(
      partText({
        content: [
          { type: "text", text: "a" },
          { type: "text", text: "b" },
        ],
      }),
    ).toBe("a\nb");
    expect(partText([{ type: "image", source: {} }])).toContain('"type": "image"');
    expect(partText("raw")).toBe("raw");
  });
});
