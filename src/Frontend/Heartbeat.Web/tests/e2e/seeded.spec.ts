import { expect, test } from "@playwright/test";

const OBS = "0192b056-7300-7000-8000-000000000001";
const OBSERVER = "0192b056-7300-7000-8000-000000000002";
const CONTENT = "0192b056-7300-7000-8000-000000000003";
const SCHEMA = "0192b056-7300-7000-8000-000000000004";
const APP = "0192b056-7300-7000-8000-000000000005";
const OBS2 = "0192b056-7300-7000-8000-000000000006";
const OBS3 = "0192b056-7300-7000-8000-000000000007";

const ents: Record<string, unknown> = {
  [OBS]: {
    category: "observation",
    references: { observerId: OBSERVER, contentId: CONTENT, schemaId: SCHEMA },
    properties: { startAt: "2026-10-17T01:00:00Z", endAt: null, timeZone: "Asia/Shanghai" },
  },
  [OBSERVER]: { category: "observer", references: {}, properties: { name: "测试 Mac" } },
  [CONTENT]: {
    category: "entity",
    schemaId: SCHEMA,
    references: { localApplicationId: APP, prevObservationId: OBS2, nextObservationId: OBS3 },
    properties: { name: "VRChat 世界 · 星野" },
  },
  [SCHEMA]: {
    category: "entity_schema",
    references: {},
    properties: { name: "VRChat 世界停留", resourceName: "vrchat-worlds", fields: {} },
  },
  [APP]: { category: "entity", schemaId: SCHEMA, references: {}, properties: { name: "VRChat" } },
  [OBS2]: {
    category: "observation",
    references: { observerId: OBSERVER, contentId: CONTENT, schemaId: SCHEMA },
    properties: { startAt: "2026-10-17T05:00:00Z", endAt: "2026-10-17T05:00:00Z", timeZone: null },
  },
  [OBS3]: {
    category: "observation",
    references: { observerId: OBSERVER, contentId: CONTENT, schemaId: SCHEMA },
    properties: { startAt: "2026-10-16T22:00:00Z", endAt: "2026-10-16T23:30:00Z", timeZone: "Asia/Shanghai" },
  },
};

test("seeded entity workspace visual check", async ({ page }) => {
  await page.route("**/api/entities/**", async (route) => {
    const url = route.request().url();
    const id = url.split("/entities/")[1]?.split("?")[0] ?? "";
    const body = ents[id];
    if (body) await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
    else await route.fulfill({ status: 404, contentType: "application/problem+json", body: "{}" });
  });

  await page.setViewportSize({ width: 1440, height: 950 });
  await page.goto(`/entities/${OBS}`);
  await page.getByRole("tab", { name: /关系图/ }).waitFor();
  await page.waitForTimeout(1200);
  await page.screenshot({ path: "shots/seed-graph.png" });

  await page.getByRole("tab", { name: "检视" }).click();
  await page.waitForTimeout(300);
  await page.screenshot({ path: "shots/seed-inspect.png", fullPage: true });

  await page.getByRole("tab", { name: "Pulse" }).click();
  await page.waitForTimeout(300);
  await page.screenshot({ path: "shots/seed-pulse.png", fullPage: true });

  await expect(page.getByText("进行中 · 结束未知").first()).toBeVisible();
});
