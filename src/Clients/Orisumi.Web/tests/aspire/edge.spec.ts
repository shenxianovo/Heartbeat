import { expect, test, type APIRequestContext, type Locator, type Page } from "@playwright/test";
import { uuidv7 } from "../../src/lib/uuid";

// 经本地环境的 Web 入口验证：页面、同源 API、Vite HMR WebSocket，以及真实数据在四种视图和实体页中的展示。
// 运行前先 `orisumi env up web`，入口地址取自 `orisumi env status --json`，默认是主检出目录的 8081。
const BASE = process.env.WEB_URL ?? "http://localhost:8081";

interface Seed {
  name: string;
  observerEntityId: string;
  /** 删除顺序：先业务实体和 Observation，再 Schema。 */
  cleanup: string[];
}

async function seed(request: APIRequestContext): Promise<Seed> {
  const stamp = Date.now();
  const name = `E2E Observer ${stamp}`;
  const now = new Date().toISOString();
  const ids = Array.from({ length: 8 }, () => uuidv7());
  const [observerSchema, observerSchemaV, contentSchema, contentSchemaV, observer, observerV, content, contentV] = ids;
  const observation = uuidv7();
  const observationV = uuidv7();
  const version = (
    entityId: string,
    versionId: string,
    category: string,
    schemaVersionId: string | null,
    body: unknown,
  ) => ({
    entityId,
    versionId,
    previousVersionId: null,
    category,
    schemaVersionId,
    content: body,
  });
  const response = await request.post(`${BASE}/api/versions`, {
    data: {
      versions: [
        version(observerSchema!, observerSchemaV!, "entity_schema", null, {
          displayName: name,
          description: "Web e2e 写入的 Observer 类型",
          resourceName: `e2e-observers-${stamp}`,
          fields: { label: { type: "string", required: true, nullable: false } },
        }),
        version(contentSchema!, contentSchemaV!, "entity_schema", null, {
          displayName: `E2E 内容 ${stamp}`,
          description: "Web e2e 写入的观测内容类型",
          resourceName: `e2e-contents-${stamp}`,
          fields: { label: { type: "string", required: true, nullable: false } },
        }),
        version(observer!, observerV!, "business", observerSchemaV!, { label: "e2e" }),
        version(content!, contentV!, "business", contentSchemaV!, { label: "e2e" }),
        version(observation, observationV, "observation", null, {
          observerVersionId: observerV,
          contentVersionId: contentV,
          inputSetVersionId: null,
          startAt: now,
          endAt: now,
          timeZone: "Asia/Shanghai",
        }),
      ],
    },
  });
  expect(response.status(), await response.text()).toBe(200);
  return {
    name,
    observerEntityId: observer!,
    cleanup: [observation, observer!, content!, observerSchema!, contentSchema!],
  };
}

async function clickAt(page: Page, selector: string) {
  let box: Awaited<ReturnType<Locator["boundingBox"]>> = null;
  await expect
    .poll(async () => {
      box = await page
        .locator(selector)
        .first()
        .boundingBox({ timeout: 1000 })
        .catch(() => null);
      return box !== null;
    })
    .toBe(true);
  const { x, y, width, height } = box!;
  await page.mouse.click(x + Math.min(width / 2, 40), y + height / 2);
}

test("web edge: page loads, same-origin API works, HMR websocket opens", async ({ page }) => {
  const wsUrls: string[] = [];
  const consoleErrors: string[] = [];
  page.on("websocket", (ws) => wsUrls.push(ws.url()));
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text());
  });

  await page.goto(`${BASE}/?source=api`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("button", { name: "心电", exact: true })).toBeVisible();

  await expect.poll(() => wsUrls.length, { timeout: 10_000 }).toBeGreaterThan(0);
  expect(wsUrls.some((u) => u.startsWith(BASE.replace(/^http/, "ws")))).toBe(true);

  const res = await page.request.get(`${BASE}/api/entities/0192b056-7300-7000-8000-0000000000ff`);
  expect(res.status()).toBe(404);

  await page.goto(`${BASE}/schemas`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("link", { name: "Schema" })).toBeVisible();

  const viteWsErrors = consoleErrors.filter((t) => /WebSocket|vite/i.test(t));
  expect(viteWsErrors).toEqual([]);
});

test("web edge: a submitted Observation appears in all four views and opens its Observer", async ({
  page,
  request,
}) => {
  const data = await seed(request);
  try {
    for (const view of ["strip", "ecg", "bubble", "ring"]) {
      await page.goto(`${BASE}/?source=api&view=${view}`);
      const mark = page.locator(`svg[data-view="${view}"] [data-key="${data.observerEntityId}"]`);
      if (view === "ecg") {
        await expect(page.locator('svg[data-view="ecg"]')).toContainText("每分钟写入");
        continue;
      }
      if (view === "strip") await expect(page.locator("svg [data-more], svg [data-key]").first()).toBeAttached();
      if (view === "strip" && !(await mark.count())) await clickAt(page, "svg [data-more]");
      await expect(mark).toBeAttached();
      await expect(mark.locator("title")).toHaveText(data.name);
    }

    await page.goto(`${BASE}/?source=api&view=strip`);
    const lane = `svg[data-view="strip"] [data-key="${data.observerEntityId}"] rect`;
    if (!(await page.locator(lane).count())) await clickAt(page, "svg [data-more]");
    await clickAt(page, lane);
    await page.locator("footer").getByRole("link", { name: "打开实体" }).click();
    await expect(page.getByRole("heading", { name: data.name })).toBeVisible();
    await expect(page.getByRole("list", { name: "被引用" }).getByRole("link")).toHaveCount(1);
  } finally {
    for (const entityId of data.cleanup) await request.delete(`${BASE}/api/entities/${entityId}`);
  }
});
