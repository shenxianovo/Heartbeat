import { expect, test, type Page } from "@playwright/test";

// 会话页：用路由拦截模拟后端，放进一天的 Claude Code 和 Codex 交互，检查会话的分组、
// 工具调用与结果的配对、视图的切换，以及只在原始记录中的工具输出。

interface Version {
  entityId: string;
  versionId: string;
  previousVersionId: null;
  category: string;
  schemaVersionId: string | null;
  recordedAt: string;
  content: Record<string, unknown>;
}

let counter = 0;
const uuid = () => `0192b056-7300-7000-8000-${(++counter).toString(16).padStart(12, "0")}`;

const day = new Date();
const at = (hours: number, minutes: number, seconds = 0) =>
  new Date(day.getFullYear(), day.getMonth(), day.getDate(), hours, minutes, seconds).toISOString();

class Backend {
  readonly versions: Version[] = [];
  readonly contents = new Map<string, string>();

  add(category: string, schemaVersionId: string | null, content: Record<string, unknown>) {
    const id = uuid();
    const version: Version = {
      entityId: id,
      versionId: uuid(),
      previousVersionId: null,
      category,
      schemaVersionId,
      recordedAt: new Date().toISOString(),
      content,
    };
    this.versions.push(version);
    return version;
  }

  raw(line: unknown) {
    const text = JSON.stringify(line);
    const digest = `sha256:${(++counter).toString(16).padStart(64, "0")}`;
    this.contents.set(digest, text);
    return { digest, mediaType: "application/json", size: text.length };
  }

  resourceOf(version: Version | undefined) {
    const schema = this.versions.find((v) => v.versionId === version?.schemaVersionId);
    return (schema?.content.resourceName as string | undefined) ?? null;
  }

  async serve(page: Page) {
    await page.route(
      (url) => url.pathname.startsWith("/api/"),
      async (route) => {
        const url = new URL(route.request().url());
        const path = url.pathname.replace(/^\/api/, "");
        const notFound = () => route.fulfill({ status: 404, json: { title: "Not Found", status: 404 } });
        if (path === "/observation-previews") return route.fulfill({ json: { previews: [] } });
        if (path === "/observations") {
          const resourceName = url.searchParams.get("resourceName");
          const versions = this.versions.filter(
            (v) =>
              v.category === "observation" &&
              (!resourceName ||
                this.resourceOf(this.versions.find((c) => c.versionId === v.content.contentVersionId)) ===
                  resourceName),
          );
          return route.fulfill({ json: { versions, next: null } });
        }
        if (path === "/entities") return route.fulfill({ json: { versions: [], next: null } });
        const [, kind, id, rest] = path.split("/");
        if (kind === "contents") {
          const body = this.contents.get(decodeURIComponent(id!));
          return body ? route.fulfill({ body, contentType: "application/json" }) : notFound();
        }
        if (kind === "versions") {
          const found = this.versions.find((v) => v.versionId === id);
          return found ? route.fulfill({ json: found }) : notFound();
        }
        if (kind === "entities" && id) {
          const found = this.versions.filter((v) => v.entityId === id);
          if (!found.length) return notFound();
          if (rest === "versions") return route.fulfill({ json: { versions: found } });
          if (rest === "referrers") return route.fulfill({ json: { referrers: [], next: null } });
          return route.fulfill({ json: found.at(-1) });
        }
        return notFound();
      },
    );
  }
}

const empty = {
  sourceRole: null,
  sourceTurnId: null,
  sourceParentId: null,
  sourceMessageId: null,
  sourceVersion: null,
  callId: null,
  toolCallVersionId: null,
  toolName: null,
  toolServer: null,
  isError: null,
  exitCode: null,
  phase: null,
  model: null,
};

function seed() {
  const backend = new Backend();
  const schema = (resourceName: string, displayName: string) =>
    backend.add("entity_schema", null, { displayName, description: displayName, resourceName, fields: {} });
  const observerSchema = schema("assistant-sessions-observers", "助手会话 Observer");
  const sessionSchema = schema("assistant-sessions", "助手会话");
  const interactionSchema = schema("assistant-interactions", "助手交互");
  const observer = backend.add("business", observerSchema.versionId, {
    platform: "macos",
    hostName: "Mac",
    programVersion: "0.1.0",
  });

  const session = (content: Record<string, unknown>) =>
    backend.add("business", sessionSchema.versionId, {
      client: null,
      parentSessionVersionId: null,
      agentName: null,
      spawnCallId: null,
      ...content,
    });
  const interact = (
    sessionVersion: Version,
    startAt: string | null,
    endAt: string,
    content: Record<string, unknown>,
  ) => {
    const version = backend.add("business", interactionSchema.versionId, {
      ...empty,
      sessionVersionId: sessionVersion.versionId,
      sourceId: uuid(),
      workingDirectory: "/Users/me/Code/Heartbeat",
      parts: [],
      sourceEntries: [],
      ...content,
    });
    backend.add("observation", null, {
      observerVersionId: observer.versionId,
      contentVersionId: version.versionId,
      inputSetVersionId: null,
      startAt,
      endAt,
      timeZone: "Asia/Shanghai",
    });
    return version;
  };
  const text = (value: string, type = "text") => ({
    type,
    text: value,
    entryIndex: 0,
    pointer: "/message/content/0/text",
    mediaType: null,
    file: null,
  });

  const claude = session({ product: "claude-code", sourceSessionId: "c1", title: "讨论助手会话的建模" });
  const codex = session({ product: "codex", sourceSessionId: "x1", title: "跑一下文档检查" });
  const child = session({
    product: "claude-code",
    sourceSessionId: "a1",
    title: null,
    parentSessionVersionId: claude.versionId,
    agentName: "web-fetch",
  });

  interact(claude, null, at(9, 0), { kind: "context", actor: "application", sourceKind: "attachment/instructions" });
  interact(claude, at(9, 1), at(9, 1), {
    kind: "message",
    actor: "user",
    sourceKind: "user",
    parts: [text("能不能把粒度细到一次 query？")],
  });
  interact(claude, null, at(9, 1, 20), {
    kind: "reasoning",
    actor: "assistant",
    sourceKind: "thinking",
    parts: [text("先看看两边的日志结构")],
  });
  interact(claude, null, at(9, 2), {
    kind: "message",
    actor: "assistant",
    sourceKind: "text",
    model: "claude-opus-5-5",
    parts: [text("可以，而且时间问题会简单很多。")],
  });
  interact(child, at(9, 1, 30), at(9, 1, 30), {
    kind: "message",
    actor: "agent",
    sourceKind: "user",
    parts: [text("读这篇文章并总结")],
  });

  const output = backend.raw({ payload: { item: { aggregated_output: "Checked 154 documents and 240 sources.\n" } } });
  interact(codex, at(10, 0), at(10, 0), {
    kind: "message",
    actor: "user",
    sourceKind: "UserMessage",
    parts: [text("跑下文档检查")],
  });
  const call = interact(codex, at(10, 0, 5), at(10, 0, 5), {
    kind: "tool-call",
    actor: "assistant",
    sourceKind: "CommandExecution",
    callId: "exec-1",
    parts: [{ ...text('["/bin/zsh","-lc","pnpm docs:check"]', "json"), pointer: "/payload/item/command" }],
    sourceEntries: [output],
  });
  interact(codex, at(10, 0, 9), at(10, 0, 9), {
    kind: "tool-result",
    actor: "tool",
    sourceKind: "CommandExecution",
    callId: "exec-1",
    toolCallVersionId: call.versionId,
    isError: false,
    exitCode: 0,
    parts: [{ ...text("", "text"), text: null, pointer: "/payload/item/aggregated_output" }],
    sourceEntries: [output],
  });
  interact(codex, at(10, 1), at(10, 1, 3), {
    kind: "message",
    actor: "assistant",
    sourceKind: "AgentMessage",
    phase: "final_answer",
    parts: [text("检查通过了。")],
  });
  return backend;
}

test("会话按最近活动排列，子 Agent 缩进在父会话下面", async ({ page }) => {
  await seed().serve(page);
  await page.goto("/sessions");
  const sessions = page.getByRole("navigation", { name: "会话" }).getByRole("link");
  await expect(sessions).toHaveCount(3);
  await expect(sessions.nth(0)).toContainText("跑一下文档检查");
  await expect(sessions.nth(1)).toContainText("讨论助手会话的建模");
  await expect(sessions.nth(2)).toContainText("子 Agent · web-fetch");
  await expect(page.getByText("3 个会话 · 你说了 2 句 · 1 次工具调用")).toBeVisible();
});

test("对话视图只有消息，过程视图把工具调用和结果合成一行", async ({ page }) => {
  await seed().serve(page);
  await page.goto("/sessions");
  await expect(page.getByText("跑下文档检查")).toBeVisible();
  await expect(page.getByText("检查通过了。")).toBeVisible();
  await expect(page.getByRole("button", { name: /CommandExecution/ })).toHaveCount(0);

  await page.getByRole("button", { name: "过程" }).click();
  const tool = page.getByRole("button", { name: /CommandExecution/ });
  await expect(tool).toHaveCount(1);
  await expect(tool).toContainText("/bin/zsh -lc pnpm docs:check");
  await tool.click();
  await expect(page.getByText("Checked 154 documents and 240 sources.")).toBeVisible();
});

test("全部视图显示上下文，推理默认收起", async ({ page }) => {
  await seed().serve(page);
  await page.goto("/sessions");
  await page.getByRole("link", { name: /讨论助手会话的建模/ }).click();
  await expect(page.getByText("可以，而且时间问题会简单很多。")).toBeVisible();
  await page.getByRole("button", { name: "全部" }).click();
  await expect(page.getByRole("button", { name: /上下文 instructions/ })).toBeVisible();
  const reasoning = page.getByRole("button", { name: /推理/ });
  await expect(reasoning).toHaveAttribute("aria-expanded", "false");
  await expect(page).toHaveURL(/view=all/);
});
