import { expect, test, type APIRequestContext } from "@playwright/test";
import { uuidv7 } from "../../src/lib/uuid";

// 经本地环境的 Web 入口验证：页面、同源 API、Vite HMR WebSocket，以及真实数据在日页面、面板、类型图、Observer 页面和实体页中的展示。
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
          fields: { label: { type: "string", description: "Smoke test label", required: true, nullable: false } },
        }),
        version(contentSchema!, contentSchemaV!, "entity_schema", null, {
          displayName: `E2E 内容 ${stamp}`,
          description: "Web e2e 写入的观测内容类型",
          resourceName: `e2e-contents-${stamp}`,
          fields: { label: { type: "string", description: "Smoke test label", required: true, nullable: false } },
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

test("web edge: page loads, same-origin API works, HMR websocket opens", async ({ page }) => {
  const wsUrls: string[] = [];
  const consoleErrors: string[] = [];
  page.on("websocket", (ws) => wsUrls.push(ws.url()));
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text());
  });

  await page.goto(`${BASE}/observers`, { waitUntil: "domcontentloaded" });
  await expect(page.getByLabel("现在的状态")).toBeVisible();

  await expect.poll(() => wsUrls.length, { timeout: 10_000 }).toBeGreaterThan(0);
  expect(wsUrls.some((u) => u.startsWith(BASE.replace(/^http/, "ws")))).toBe(true);

  const res = await page.request.get(`${BASE}/api/entities/0192b056-7300-7000-8000-0000000000ff`);
  expect(res.status()).toBe(404);

  await page.goto(`${BASE}/entities`, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("link", { name: "实体", exact: true })).toBeVisible();

  const viteWsErrors = consoleErrors.filter((t) => /WebSocket|vite/i.test(t));
  expect(viteWsErrors).toEqual([]);
});

test("web edge: a submitted Observation gets a row on the Observer page and opens its Observer", async ({
  page,
  request,
}) => {
  const data = await seed(request);
  try {
    await page.goto(`${BASE}/observers`);
    const row = page.getByRole("listitem").filter({ hasText: data.name });
    await expect(row).toContainText("今天 1 份");
    await row.getByRole("button").click();
    const panel = page.getByRole("region", { name: new RegExp(data.name) });
    await expect(panel.getByRole("heading", { name: data.name })).toBeVisible();
    await panel.getByRole("link", { name: "在检查器中打开 →" }).click();
    await expect(page.getByRole("heading", { name: data.name })).toBeVisible();
    await expect(page.getByRole("list", { name: "被引用" }).getByRole("link")).toHaveCount(1);
  } finally {
    for (const entityId of data.cleanup) await request.delete(`${BASE}/api/entities/${entityId}`);
  }
});

/** 一个事物、引用它的观测内容和一份 8 分钟的时段，和前台状态 Observer 的结构相同。 */
async function seedThing(request: APIRequestContext) {
  const stamp = Date.now();
  const id = () => uuidv7();
  const [thingSchema, thingSchemaV, periodSchema, periodSchemaV, observerSchema, observerSchemaV] = [
    id(),
    id(),
    id(),
    id(),
    id(),
    id(),
  ];
  const [thing, thingV, period, periodV, observer, observerV, observation, observationV] = [
    id(),
    id(),
    id(),
    id(),
    id(),
    id(),
    id(),
    id(),
  ];
  const names = {
    thing: `e2e.thing.${stamp}`,
    thingType: `E2E 事物 ${stamp}`,
    thingResource: `e2e-things-${stamp}`,
    seenAs: `E2E 应用 ${stamp}`,
    observer: `E2E 观测者 ${stamp}`,
  };
  const string = (description: string) => ({ type: "string", description, required: true, nullable: false });
  const v = (
    entityId: string,
    versionId: string,
    category: string,
    schemaVersionId: string | null,
    content: unknown,
  ) => ({
    entityId,
    versionId,
    previousVersionId: null,
    category,
    schemaVersionId,
    content,
  });
  const end = Date.now() - 2 * 60_000;
  const response = await request.post(`${BASE}/api/versions`, {
    data: {
      versions: [
        v(thingSchema, thingSchemaV, "entity_schema", null, {
          displayName: names.thingType,
          description: "Web e2e 写入的事物类型",
          resourceName: names.thingResource,
          fields: { key: string("事物的标识") },
        }),
        v(periodSchema, periodSchemaV, "entity_schema", null, {
          displayName: `E2E 时段 ${stamp}`,
          description: "Web e2e 写入的时段类型",
          resourceName: `e2e-periods-${stamp}`,
          fields: {
            name: string("当时读到的名字"),
            thingVersionId: { type: "reference", description: "这一段关于的事物", required: true, nullable: false },
          },
        }),
        v(observerSchema, observerSchemaV, "entity_schema", null, {
          displayName: names.observer,
          description: "Web e2e 写入的 Observer 类型",
          resourceName: `e2e-thing-observers-${stamp}`,
          fields: { label: string("标签") },
        }),
        v(thing, thingV, "business", thingSchemaV, { key: names.thing }),
        v(observer, observerV, "business", observerSchemaV, { label: "e2e" }),
        v(period, periodV, "business", periodSchemaV, { name: names.seenAs, thingVersionId: thingV }),
        v(observation, observationV, "observation", null, {
          observerVersionId: observerV,
          contentVersionId: periodV,
          inputSetVersionId: null,
          startAt: new Date(end - 8 * 60_000).toISOString(),
          endAt: new Date(end).toISOString(),
          timeZone: "Asia/Shanghai",
        }),
      ],
    },
  });
  expect(response.status(), await response.text()).toBe(200);
  return { names, cleanup: [observation, period, observer, thing, periodSchema, observerSchema, thingSchema] };
}

test("web edge: a thing appears in the day ranking, opens stacked panels, shows on the type map and grows a weave", async ({
  page,
  request,
}) => {
  const data = await seedThing(request);
  try {
    await page.goto(`${BASE}/`);
    const ranking = page.getByRole("list", { name: "排行" });
    await expect(ranking).toContainText(data.names.thing);
    await expect(ranking).toContainText("8 分钟");
    await expect(page.getByRole("img", { name: "这一天的时间线" })).toBeVisible();

    // 点排行里的记号推出事物的面板，里面有引用它的时段读到的名字。
    await ranking.getByRole("button", { name: new RegExp(data.names.thing) }).click();
    const panels = page.getByRole("complementary", { name: "面板" });
    await expect(panels.getByRole("heading", { name: data.names.thing })).toBeVisible();
    await expect(page).toHaveURL(/stack=/);
    await expect(panels).toContainText(data.names.seenAs);

    // 面板里的记号接着打开下一层，Esc 关掉最后一层。
    await panels.getByRole("button", { name: new RegExp(`^${data.names.observer}`) }).click();
    await expect(panels.getByRole("region")).toHaveCount(2);
    await page.keyboard.press("Escape");
    await expect(panels.getByRole("region")).toHaveCount(1);

    // 类型图里事物在“事物”一列，选中后列出它的实体。
    await page.goto(`${BASE}/entities?type=${data.names.thingResource}`);
    await expect(page.getByRole("button", { name: new RegExp(data.names.thingType), pressed: true })).toBeVisible();
    await expect(page.getByRole("heading", { name: data.names.thingType })).toBeVisible();
    await expect(page.getByRole("button", { name: new RegExp(data.names.thing) }).first()).toBeVisible();

    // 点类型下的实体，从它开始织：事物左边长出引用它的时段（并着记下它的 Observation），
    // 时段左边长出 Observer；停在 Observer 上时，路上的标签亮起。
    await page
      .getByRole("button", { name: new RegExp(data.names.thing) })
      .first()
      .click();
    await expect(page).toHaveURL(/root=/);
    await page.getByRole("button", { name: new RegExp(`${data.names.thingType} ${data.names.thing}`) }).click();
    const period = page.getByRole("button", { name: new RegExp(data.names.seenAs) });
    await expect(period).toBeVisible();
    await expect(period).toContainText(data.names.observer);
    await period.click();
    const observer = page.getByRole("button", { name: new RegExp(data.names.observer) }).last();
    await expect(observer).toBeVisible();
    await observer.hover();
    await expect(page.locator("svg text", { hasText: /^来源$/ })).toBeVisible();
    await expect(page.locator("svg text", { hasText: /^thing$/ })).toBeVisible();
  } finally {
    for (const entityId of data.cleanup) await request.delete(`${BASE}/api/entities/${entityId}`);
  }
});
