import { expect, test } from "@playwright/test";

import {
  customTrack,
  desktopTrack,
  identityRoutes,
  recordingRoutes,
  seedSession,
  userStorageKey,
} from "./fixtures";

test.beforeEach(async ({ page }) => {
  await seedSession(page);
  await identityRoutes(page);
});

test("统一时间线自动读取完整区间并可查看原始记录", async ({ page }) => {
  const requests = await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByText("com.apple.finder", { exact: true })).toBeVisible();
  await expect(page.getByText("com.microsoft.VSCode", { exact: true })).toBeVisible();
  await page.getByTitle("时间区间", { exact: true }).click();
  await page.getByRole("button", { name: "查看详情" }).click();
  await expect(page.getByText(/malformed-observation/).first()).toBeVisible();
  await expect(page.getByText("原始 JSON", { exact: true }).first()).toBeVisible();
  const first = requests.find((url) => url.pathname.endsWith("/records"))!;
  const next = requests.find((url) => url.searchParams.has("cursor"))!;
  expect(next.searchParams.get("cursor")).toBe("next-page-test-cursor");
  expect(next.searchParams.get("from")).toBe(first.searchParams.get("from"));
  expect(next.searchParams.get("to")).toBe(first.searchParams.get("to"));
  await page.screenshot({ path: "test-results/replay-desktop.png", fullPage: true });
});

test("未知类型安全展示 JSON，窄屏也可查看", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await recordingRoutes(page);
  await page.goto("/");
  await page.getByLabel("采集来源").selectOption(customTrack.collectorId);
  await page.getByRole("button", { name: /example\.observation.*1 条记录/ }).click();
  await expect(page.getByText(/<script>window.untrustedExecuted=true<\/script>/)).toBeVisible();
  await expect(page.getByText("瞬时记录", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => "untrustedExecuted" in window)).toBe(false);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({ path: "test-results/replay-mobile.png", fullPage: true });
});

test("空目录有明确状态", async ({ page }) => {
  await recordingRoutes(page, { empty: true });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "还没有可以回放的记录" })).toBeVisible();
});

test("请求失败后可重试恢复", async ({ page }) => {
  await recordingRoutes(page, { fail: true });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "暂时无法读取采集来源" })).toBeVisible({
    timeout: 15000,
  });
  await recordingRoutes(page);
  await page.getByRole("button", { name: "重试" }).click();
  await expect(page.getByText("com.apple.finder", { exact: true })).toBeVisible();
});

test("退出后清理会话，返回首页不能继续查看缓存记录", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await expect(page.getByText("com.apple.finder", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "退出登录" }).click();
  await expect(page).toHaveURL(/\/login/);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), userStorageKey)).toBeNull();
  await page.goto("/");
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByText("com.apple.finder", { exact: true })).toHaveCount(0);
});

test("重叠区间分别可见且可以直接展开", async ({ page }) => {
  await recordingRoutes(page);
  await page.route(`**/api/v1/tracks/${desktopTrack.id}/records?**`, async (route) => {
    const from = Date.parse(new URL(route.request().url()).searchParams.get("from")!);
    const records = ["Overlap A", "Overlap B"].map((name, index) => ({
      id: `overlap-${index}`,
      startedAt: new Date(from + 4 * 3_600_000).toISOString(),
      endedAt: new Date(from + 8 * 3_600_000).toISOString(),
      observedAt: null,
      receivedAt: new Date(from + 8 * 3_600_000).toISOString(),
      value: {
        device_id: "test-mac",
        application: { platform: "macos", id_kind: "bundle_id", id: name },
      },
    }));
    await route.fulfill({ json: { track: desktopTrack, records, nextCursor: null } });
  });
  await page.goto("/");
  const first = page.getByTitle("Overlap A", { exact: true });
  const second = page.getByTitle("Overlap B", { exact: true });
  await expect(first).toBeVisible();
  await expect(second).toBeVisible();
  const a = (await first.boundingBox())!;
  const b = (await second.boundingBox())!;
  expect(a.y + a.height <= b.y || b.y + b.height <= a.y).toBe(true);
  await first.click();
  await expect(page.getByText("所选区间")).toBeVisible();
  await page.screenshot({ path: "test-results/replay-overlapping.png", fullPage: true });
});

test("来源可以组合选择，空选择不会一直加载", async ({ page }) => {
  await recordingRoutes(page);
  await page.goto("/");
  await page.getByLabel("采集来源").selectOption([]);
  await expect(page.getByRole("heading", { name: "尚未选择来源" })).toBeVisible();
  await page
    .getByLabel("采集来源")
    .selectOption([desktopTrack.collectorId, customTrack.collectorId]);
  await expect(page.getByTitle("com.apple.finder", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /example\.observation.*1 条记录/ })).toBeVisible();
});
